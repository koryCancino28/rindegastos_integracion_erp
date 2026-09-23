using Microsoft.Extensions.Options;
using Rindegastos.Worker.Api;
using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Configuracion;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Ciclo de la ENTREGA DE FONDOS -> Ingreso de Comprobante (transferencia).
///
///   Paso 0  Reparacion     reintenta marcar en Rindegastos lo ya contabilizado
///   Paso 1  Descarga       trae los fondos no integrados y guarda cada deposito
///   Paso 2  Homologacion   resuelve cuenta, persona, banco y tipo de cambio
///   Paso 3  Contabilizacion crea el comprobante tipo 19 en el ERP
///   Paso 4  Confirmacion   marca el fondo cuando TODOS sus depositos estan hechos
///
/// La unidad de trabajo es el DEPOSITO: la entrega inicial y cada recarga son
/// transferencias distintas. La marca de integracion, en cambio, es del fondo
/// completo, asi que solo se marca cuando no le queda ningun deposito pendiente.
/// </summary>
public sealed class ServicioCicloFondo
{
    private readonly ClienteRindegastos _api;
    private readonly RepositorioStagingFondo _staging;
    private readonly RepositorioComprobante _comprobantes;
    private readonly ServicioHomologacionFondo _homologacion;
    private readonly ServicioAsientoFondo _asiento;
    private readonly OpcionesIntegracion _opciones;
    private readonly ILogger<ServicioCicloFondo> _logger;

    public ServicioCicloFondo(
        ClienteRindegastos api,
        RepositorioStagingFondo staging,
        RepositorioComprobante comprobantes,
        ServicioHomologacionFondo homologacion,
        ServicioAsientoFondo asiento,
        IOptions<OpcionesIntegracion> opciones,
        ILogger<ServicioCicloFondo> logger)
    {
        _api = api;
        _staging = staging;
        _comprobantes = comprobantes;
        _homologacion = homologacion;
        _asiento = asiento;
        _opciones = opciones.Value;
        _logger = logger;
    }

    public async Task EjecutarAsync(CancellationToken ct)
    {
        await ConfirmarPendientesAsync(ct);
        await DescargarAsync(ct);
        await HomologarAsync(ct);

        if (!_opciones.SoloLectura)
        {
            await ContabilizarAsync(ct);
            await ConfirmarPendientesAsync(ct);
        }
    }

    // ------------------------------------------------------------------ Paso 0 y 4

    private async Task ConfirmarPendientesAsync(CancellationToken ct)
    {
        var marcas = await _staging.ObtenerPendientesDeConfirmarAsync(ct);
        if (marcas.Count == 0) return;

        _logger.LogInformation("Marcando {N} fondos ya contabilizados en Rindegastos", marcas.Count);

        try
        {
            await _api.MarcarFondosIntegradosAsync(marcas, ct);
            await _staging.MarcarConfirmadoAsync(marcas.Select(m => m.Id), ct);
        }
        catch (Exception ex)
        {
            // Los depositos quedan en estado 2, que es lo que impide volver a
            // contabilizarlos. Se reintenta en el proximo ciclo.
            _logger.LogError(ex, "Fallo la confirmacion de fondos en Rindegastos. " +
                                 "Se reintentara en el proximo ciclo.");
        }
    }

    // ------------------------------------------------------------------ Paso 1

    private async Task DescargarAsync(CancellationToken ct)
    {
        var fondos = await _api.ObtenerFondosAsync(integrationStatus: 0, ct);
        if (fondos.Count == 0)
        {
            _logger.LogInformation("No hay fondos nuevos por descargar.");
            return;
        }

        DateTime? desde = DateTime.TryParse(_opciones.FechaDesde, out var d) ? d.Date : null;

        int nuevos = 0, omitidos = 0;

        foreach (var fondo in fondos)
        {
            // Los fondos que nacen de una solicitud aprobada los registra el flujo
            // de solicitudes, que es el que tiene el DNI. Si entraran tambien por
            // aqui, la misma plata se registraria dos veces.
            if (fondo.VieneDeSolicitud)
            {
                omitidos++;
                _logger.LogInformation(
                    "Fondo {Id} '{Titulo}' omitido: lo creo la solicitud {Solicitud}, que se registra aparte.",
                    fondo.Id, fondo.Title, fondo.FundRequestId);
                continue;
            }

            // Sin cuenta contable en 'Descripcion' no es un fondo de este flujo:
            // son los fondos viejos y de prueba, que traen texto libre o nada.
            // No se guardan para no llenar el staging de filas en error.
            if (!ParecCuentaContable(fondo.Description))
            {
                omitidos++;
                _logger.LogInformation(
                    "Fondo {Id} '{Titulo}' omitido: su campo 'Descripcion' ('{Desc}') no es una cuenta contable.",
                    fondo.Id, fondo.Title, fondo.Description);
                continue;
            }

            var depositos = fondo.Depositos;
            var existentes = await _staging.ObtenerDepositosExistentesAsync(fondo.Id, ct);

            for (var i = 0; i < depositos.Count; i++)
            {
                var numero = (short)(i + 1);
                if (existentes.Contains(numero)) continue;

                var deposito = depositos[i];
                if (desde is not null && deposito.TransactionDate is not null
                    && deposito.TransactionDate.Value.Date < desde.Value)
                    continue;

                try
                {
                    await _staging.InsertarAsync(fondo, numero, deposito, ct);
                    nuevos++;

                    _logger.LogInformation(
                        "Fondo {Id} '{Titulo}': deposito {N} de {Monto:N2} {Moneda} del {Fecha:yyyy-MM-dd} guardado",
                        fondo.Id, fondo.Title, numero, deposito.TransactionAmount,
                        deposito.CurrencyCode ?? fondo.Currency, deposito.TransactionDate);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "No se pudo guardar en staging el deposito {N} del fondo {Id}",
                                     numero, fondo.Id);
                }
            }
        }

        _logger.LogInformation("Fondos: {Total} recibidos, {Nuevos} depositos nuevos, {Omitidos} fondos omitidos",
                               fondos.Count, nuevos, omitidos);
    }

    /// <summary>
    /// Description tiene que ser el codigo de una cuenta contable: solo digitos.
    /// Los fondos viejos traen ahi texto libre ("pruebas", "VIAJE TIENDA PIURA")
    /// o el DNI de la persona, y esos no son de este flujo.
    /// </summary>
    private static bool ParecCuentaContable(string? descripcion)
    {
        var t = descripcion?.Trim();
        return !string.IsNullOrWhiteSpace(t) && t.Length >= 6 && t.All(char.IsDigit);
    }

    // ------------------------------------------------------------------ Paso 2

    private async Task HomologarAsync(CancellationToken ct)
    {
        var pendientes = await _staging.ObtenerPorEstadoAsync(EstadoFondo.Descargado, ct);
        if (pendientes.Count == 0) return;

        _logger.LogInformation("Homologando {N} depositos de fondos", pendientes.Count);
        var ok = 0;

        foreach (var (idFondo, deposito) in pendientes)
        {
            var enStaging = await _staging.ObtenerAsync(idFondo, deposito, ct);
            if (enStaging is null)
            {
                await _staging.RegistrarErrorAsync(idFondo, deposito,
                    "No se pudo leer el JSON del fondo.", _opciones.MaxIntentos, ct);
                continue;
            }

            var resultado = await _homologacion.HomologarAsync(
                enStaging.Fondo, deposito, enStaging.Transaccion, ct);

            if (resultado.Ok)
            {
                await _staging.GuardarHomologacionAsync(resultado.Fondo!, ct);
                ok++;
            }
            else
            {
                var mensaje = string.Join(" | ", resultado.Errores);
                _logger.LogWarning("Fondo {Id} deposito {N} no homologado: {Errores}", idFondo, deposito, mensaje);
                await _staging.RegistrarErrorAsync(idFondo, deposito, mensaje, _opciones.MaxIntentos, ct);
            }
        }

        _logger.LogInformation("Homologacion de fondos terminada: {Ok} de {Total} correctos", ok, pendientes.Count);
    }

    // ------------------------------------------------------------------ Paso 3

    private async Task ContabilizarAsync(CancellationToken ct)
    {
        var pendientes = await _staging.ObtenerPorEstadoAsync(EstadoFondo.Homologado, ct);
        if (pendientes.Count == 0) return;

        if (_opciones.CodUsuarioErp <= 0)
        {
            _logger.LogError("Integracion:CodUsuarioErp no esta configurado. No se contabiliza nada.");
            return;
        }

        _logger.LogInformation("Contabilizando {N} depositos de fondos", pendientes.Count);

        foreach (var (idFondo, deposito) in pendientes)
        {
            try
            {
                var enStaging = await _staging.ObtenerAsync(idFondo, deposito, ct);
                if (enStaging is null) continue;

                // Se vuelve a homologar para trabajar con datos frescos, igual que
                // en el flujo de informes: el tipo de cambio y el cierre contable
                // pudieron cambiar desde la primera vez.
                var resultado = await _homologacion.HomologarAsync(
                    enStaging.Fondo, deposito, enStaging.Transaccion, ct);

                if (!resultado.Ok)
                {
                    await _staging.RegistrarErrorAsync(idFondo, deposito,
                        string.Join(" | ", resultado.Errores), _opciones.MaxIntentos, ct);
                    continue;
                }

                var res = await _comprobantes.ContabilizarFondoAsync(
                    resultado.Fondo!, _asiento, _opciones.CodUsuarioErp, ct);

                await _staging.MarcarContabilizadoAsync(
                    idFondo, deposito, res.CodComprobante, res.FolioComprobante, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo la contabilizacion del deposito {N} del fondo {Id}", deposito, idFondo);
                await _staging.RegistrarErrorAsync(idFondo, deposito, ex.Message, _opciones.MaxIntentos, ct);
            }
        }
    }
}
