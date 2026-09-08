using Microsoft.Extensions.Options;
using Rindegastos.Worker.Api;
using Rindegastos.Worker.Configuracion;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Ejecuta un ciclo completo de integracion.
///
///   Paso 0  Reparacion    reintenta confirmar en Rindegastos lo que ya se contabilizo
///   Paso 1  Descarga      trae los gastos aprobados y no integrados a staging
///   Paso 2  Homologacion  resuelve proveedor, cuenta, centro de costo, moneda y montos
///   Paso 3  Contabilizacion  crea la factura y el comprobante en el ERP
///   Paso 4  Confirmacion  devuelve el numero de comprobante a Rindegastos
///
/// El paso 0 va primero a proposito: si en el ciclo anterior se contabilizo pero
/// fallo la llamada a la API, ese gasto seguiria apareciendo como "no integrado"
/// y se contabilizaria dos veces. Reintentar la confirmacion antes de descargar
/// cierra esa ventana.
/// </summary>
public sealed class ServicioCiclo
{
    private readonly ClienteRindegastos _api;
    private readonly RepositorioStaging _staging;
    private readonly RepositorioComprobante _comprobantes;
    private readonly ServicioHomologacion _homologacion;
    private readonly ServicioAsiento _asiento;
    private readonly OpcionesIntegracion _opciones;
    private readonly ILogger<ServicioCiclo> _logger;

    public ServicioCiclo(
        ClienteRindegastos api,
        RepositorioStaging staging,
        RepositorioComprobante comprobantes,
        ServicioHomologacion homologacion,
        ServicioAsiento asiento,
        IOptions<OpcionesIntegracion> opciones,
        ILogger<ServicioCiclo> logger)
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
        _logger.LogInformation("===== Inicio de ciclo (SoloLectura = {Modo}) =====", _opciones.SoloLectura);

        await ConfirmarPendientesAsync(ct);
        await DescargarAsync(ct);
        await HomologarAsync(ct);

        if (_opciones.SoloLectura)
        {
            _logger.LogWarning(
                "MODO SOLO LECTURA: no se contabiliza nada. " +
                "Cambiar Integracion:SoloLectura a false cuando la validacion este conforme.");
        }
        else
        {
            await ContabilizarAsync(ct);
            await ConfirmarPendientesAsync(ct);
        }

        _logger.LogInformation("===== Fin de ciclo =====");
    }

    // ------------------------------------------------------------------ Paso 0 y 4

    /// <summary>Marca en Rindegastos los gastos que ya tienen comprobante en el ERP.</summary>
    private async Task ConfirmarPendientesAsync(CancellationToken ct)
    {
        var marcas = await _staging.ObtenerPendientesDeConfirmarAsync(ct);
        if (marcas.Count == 0) return;

        _logger.LogInformation("Confirmando {N} gastos ya contabilizados en Rindegastos", marcas.Count);

        try
        {
            await _api.MarcarGastosIntegradosAsync(marcas, ct);
            await _staging.MarcarConfirmadoAsync(marcas.Select(m => m.Id), ct);
        }
        catch (Exception ex)
        {
            // Se reintenta en el proximo ciclo. El gasto queda en estado CONTABILIZADO,
            // que es justamente lo que impide volver a contabilizarlo.
            _logger.LogError(ex, "Fallo la confirmacion en Rindegastos. Se reintentara en el proximo ciclo.");
        }
    }

    // ------------------------------------------------------------------ Paso 1

    private async Task DescargarAsync(CancellationToken ct)
    {
        var gastos = await _api.ObtenerGastosAsync(
            _opciones.StatusGasto, integrationStatus: 0, _opciones.FechaDesde, ct);

        if (gastos.Count == 0)
        {
            _logger.LogInformation("No hay gastos nuevos por descargar.");
            return;
        }

        // Si el Id ya esta en staging, se ignora. Es la garantia anti duplicados.
        var existentes = await _staging.ObtenerIdsExistentesAsync(gastos.Select(g => g.Id), ct);
        var nuevos = gastos.Where(g => !existentes.Contains(g.Id)).ToList();

        _logger.LogInformation("Descargados {Total} gastos: {Nuevos} nuevos, {Repetidos} ya conocidos",
            gastos.Count, nuevos.Count, gastos.Count - nuevos.Count);

        foreach (var g in nuevos)
        {
            try
            {
                await _staging.InsertarAsync(g, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar en staging el gasto {Id}", g.Id);
            }
        }
    }

    // ------------------------------------------------------------------ Paso 2

    private async Task HomologarAsync(CancellationToken ct)
    {
        var ids = await _staging.ObtenerIdsPorEstadoAsync(EstadoGasto.Descargado, ct);
        if (ids.Count == 0) return;

        _logger.LogInformation("Homologando {N} gastos", ids.Count);
        var ok = 0;

        foreach (var id in ids)
        {
            var gasto = await _staging.ObtenerGastoAsync(id, ct);
            if (gasto is null)
            {
                await _staging.RegistrarErrorAsync(id, "No se pudo leer el JSON del gasto.", _opciones.MaxIntentos, ct);
                continue;
            }

            var resultado = await _homologacion.HomologarAsync(gasto, ct);
            if (resultado.Ok)
            {
                await _staging.GuardarHomologacionAsync(resultado.Gasto!, ct);
                ok++;
            }
            else
            {
                var mensaje = string.Join(" | ", resultado.Errores);
                _logger.LogWarning("Gasto {Id} no homologado: {Errores}", id, mensaje);
                await _staging.RegistrarErrorAsync(id, mensaje, _opciones.MaxIntentos, ct);
            }
        }

        _logger.LogInformation("Homologacion terminada: {Ok} de {Total} correctos", ok, ids.Count);
    }

    // ------------------------------------------------------------------ Paso 3

    private async Task ContabilizarAsync(CancellationToken ct)
    {
        var ids = await _staging.ObtenerIdsPorEstadoAsync(EstadoGasto.Homologado, ct);
        if (ids.Count == 0) return;

        if (_opciones.CodUsuarioErp <= 0)
        {
            _logger.LogError("Integracion:CodUsuarioErp no esta configurado. No se contabiliza nada.");
            return;
        }

        _logger.LogInformation("Contabilizando {N} gastos", ids.Count);

        foreach (var id in ids)
        {
            try
            {
                var gastoApi = await _staging.ObtenerGastoAsync(id, ct);
                if (gastoApi is null) continue;

                // Se vuelve a homologar para trabajar con datos frescos:
                // la fecha de contabilizacion y el cierre contable pudieron cambiar.
                var resultado = await _homologacion.HomologarAsync(gastoApi, ct);
                if (!resultado.Ok)
                {
                    var mensaje = string.Join(" | ", resultado.Errores);
                    await _staging.RegistrarErrorAsync(id, mensaje, _opciones.MaxIntentos, ct);
                    continue;
                }

                var g = resultado.Gasto!;
                var lineas = await _asiento.ConstruirAsync(g, ct);
                var res = await _comprobantes.ContabilizarAsync(g, lineas, _opciones.CodUsuarioErp, ct);

                await _staging.MarcarContabilizadoAsync(id, res.CodFacturaBoleta, res.CodComprobante, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo la contabilizacion del gasto {Id}", id);
                await _staging.RegistrarErrorAsync(id, ex.Message, _opciones.MaxIntentos, ct);
            }
        }
    }
}
