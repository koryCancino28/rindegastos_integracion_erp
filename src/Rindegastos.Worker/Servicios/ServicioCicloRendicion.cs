using Microsoft.Extensions.Options;
using Rindegastos.Worker.Api;
using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Configuracion;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Ciclo de los INFORMES (rendiciones) -> Ingreso de Comprobante.
///
///   Paso 0  Reparacion     reintenta confirmar en Rindegastos lo ya contabilizado
///   Paso 1  Descarga       trae los informes CERRADOS (Status 1) y no integrados,
///                          con sus gastos, y refresca los que ya estaban en staging
///   Paso 2  Homologacion   resuelve tipo de rendicion, persona, cuentas y numero
///   Paso 3  Contabilizacion crea el comprobante en el ERP
///   Paso 4  Confirmacion   marca el informe y sus planillas de movilidad
///
/// Todo informe cerrado da UN comprobante (Diario o Caja Egreso segun el tipo de
/// rendicion) con una linea por gasto mas la contrapartida:
///   - planilla de movilidad: entra solo por aqui, a la cuenta de gasto.
///   - factura, boleta o RxH: primero entra por compras (el otro flujo, que corre
///     antes en el mismo ciclo) y aqui se cancela contra la cuenta del proveedor.
/// </summary>
public sealed class ServicioCicloRendicion
{
    private readonly ClienteRindegastos _api;
    private readonly RepositorioStagingInforme _staging;
    private readonly RepositorioComprobante _comprobantes;
    private readonly ServicioHomologacionInforme _homologacion;
    private readonly ServicioAsientoInforme _asiento;
    private readonly OpcionesIntegracion _opciones;
    private readonly ILogger<ServicioCicloRendicion> _logger;

    public ServicioCicloRendicion(
        ClienteRindegastos api,
        RepositorioStagingInforme staging,
        RepositorioComprobante comprobantes,
        ServicioHomologacionInforme homologacion,
        ServicioAsientoInforme asiento,
        IOptions<OpcionesIntegracion> opciones,
        ILogger<ServicioCicloRendicion> logger)
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

    /// <summary>
    /// Marca en Rindegastos los informes que ya tienen comprobante en el ERP,
    /// junto con todos sus gastos.
    /// </summary>
    private async Task ConfirmarPendientesAsync(CancellationToken ct)
    {
        var pendientes = await _staging.ObtenerPendientesDeConfirmarAsync(ct);
        if (pendientes.Count == 0) return;

        _logger.LogInformation("Confirmando {N} informes ya contabilizados en Rindegastos", pendientes.Count);

        try
        {
            // Primero los gastos, despues el informe. Si se corta a la mitad,
            // el informe sigue en estado 2 y se reintenta completo el proximo ciclo.
            var marcasGastos = pendientes
                .SelectMany(p => p.IdsGastos.Select(idGasto => new MarcaIntegracion
                {
                    Id = idGasto,
                    IntegrationStatus = ConstantesMarcaIntegracion.Integrado,
                    IntegrationCode = p.Marca.IntegrationCode,
                    IntegrationDate = p.Marca.IntegrationDate
                }))
                .ToList();

            if (marcasGastos.Count > 0)
                await _api.MarcarGastosIntegradosAsync(marcasGastos, ct);

            await _api.MarcarInformesIntegradosAsync(pendientes.Select(p => p.Marca).ToList(), ct);
            await _staging.MarcarConfirmadoAsync(pendientes.Select(p => p.Marca.Id), ct);
        }
        catch (Exception ex)
        {
            // El informe queda en estado 2, que es justo lo que impide volver a
            // contabilizarlo. Se reintenta en el proximo ciclo.
            _logger.LogError(ex, "Fallo la confirmacion de informes en Rindegastos. " +
                                 "Se reintentara en el proximo ciclo.");
        }
    }

    // ------------------------------------------------------------------ Paso 1

    private async Task DescargarAsync(CancellationToken ct)
    {
        // StatusInforme es propio de este flujo: en informes 0 es "abierto" y 1
        // es "cerrado", nada que ver con el 1 = "aprobado" de los gastos.
        var informes = await _api.ObtenerInformesAsync(
            _opciones.StatusInforme, integrationStatus: 0, _opciones.FechaDesde, ct);

        if (informes.Count == 0)
        {
            _logger.LogInformation("No hay informes nuevos por descargar.");
            return;
        }

        // Solo interesan los informes cuyo tipo de rendicion sabemos contabilizar.
        // Los demas se ignoran en silencio: pueden ser de otro proceso.
        var reconocidos = informes
            .Where(i => ReglaRendicion.Reconocer(i.TipoRendicionTexto) is not null)
            .ToList();

        var ids = reconocidos.Select(i => i.Id).ToList();
        var existentes = await _staging.ObtenerIdsExistentesAsync(ids, ct);
        var sinComprobante = await _staging.ObtenerIdsSinComprobanteAsync(ids, ct);

        var nuevos = reconocidos.Where(i => !existentes.Contains(i.Id)).ToList();

        // Los que ya estaban en staging sin comprobante se vuelven a leer: pudieron
        // descargarse cuando el informe estaba abierto y ahora vienen cerrados, o
        // pudieron corregirse en Rindegastos.
        var porRefrescar = reconocidos.Where(i => sinComprobante.Contains(i.Id)).ToList();

        _logger.LogInformation(
            "Informes: {Total} recibidos, {Reconocidos} de rendicion, {Nuevos} nuevos, {Refrescar} ya conocidos " +
            "sin comprobante (se refrescan)",
            informes.Count, reconocidos.Count, nuevos.Count, porRefrescar.Count);

        foreach (var informe in nuevos)
        {
            try
            {
                // Los gastos se piden por ReportId, que es como la API enlaza
                // un gasto con su informe.
                var gastos = await _api.ObtenerGastosPorInformeAsync(informe.Id, ct);
                await _staging.InsertarAsync(informe, gastos, ct);

                _logger.LogInformation("Informe {Id} '{Titulo}' guardado con {N} gastos ({Pl} planillas de movilidad)",
                    informe.Id, informe.Title, gastos.Count, gastos.Count(g => g.EsPlanillaMovilidad));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar en staging el informe {Id}", informe.Id);
            }
        }

        foreach (var informe in porRefrescar)
        {
            try
            {
                var gastos = await _api.ObtenerGastosPorInformeAsync(informe.Id, ct);
                if (await _staging.RefrescarAsync(informe, gastos, ct))
                    _logger.LogInformation("Informe {Id} '{Titulo}' cambio en Rindegastos (Status {Status}); " +
                                           "se vuelve a procesar", informe.Id, informe.Title, informe.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo refrescar en staging el informe {Id}", informe.Id);
            }
        }
    }

    // ------------------------------------------------------------------ Paso 2

    private async Task HomologarAsync(CancellationToken ct)
    {
        var ids = await _staging.ObtenerIdsPorEstadoAsync(EstadoInforme.Descargado, ct);
        if (ids.Count == 0) return;

        _logger.LogInformation("Homologando {N} informes", ids.Count);
        var ok = 0;

        foreach (var id in ids)
        {
            var enStaging = await _staging.ObtenerAsync(id, ct);
            if (enStaging is null)
            {
                await _staging.RegistrarErrorAsync(id, "No se pudo leer el JSON del informe.",
                                                   _opciones.MaxIntentos, null, ct);
                continue;
            }

            var resultado = await _homologacion.HomologarAsync(enStaging.Informe, enStaging.Gastos, ct);
            if (resultado.MotivoEspera is not null)
            {
                _logger.LogInformation("Informe {Id} en espera: {Motivo}", id, resultado.MotivoEspera);
                await _staging.MarcarEnEsperaAsync(id, resultado.MotivoEspera, resultado.DocumentoEmpleado, ct);
            }
            else if (resultado.Ok)
            {
                await _staging.GuardarHomologacionAsync(resultado.Informe!, ct);
                ok++;
            }
            else
            {
                var mensaje = string.Join(" | ", resultado.Errores);
                _logger.LogWarning("Informe {Id} no homologado: {Errores}", id, mensaje);
                await _staging.RegistrarErrorAsync(id, mensaje, _opciones.MaxIntentos,
                                                   resultado.DocumentoEmpleado, ct);
            }
        }

        _logger.LogInformation("Homologacion de informes terminada: {Ok} de {Total} correctos", ok, ids.Count);
    }

    // ------------------------------------------------------------------ Paso 3

    private async Task ContabilizarAsync(CancellationToken ct)
    {
        var ids = await _staging.ObtenerIdsPorEstadoAsync(EstadoInforme.Homologado, ct);
        if (ids.Count == 0) return;

        if (_opciones.CodUsuarioErp <= 0)
        {
            _logger.LogError("Integracion:CodUsuarioErp no esta configurado. No se contabiliza nada.");
            return;
        }

        _logger.LogInformation("Contabilizando {N} informes", ids.Count);

        foreach (var id in ids)
        {
            try
            {
                var enStaging = await _staging.ObtenerAsync(id, ct);
                if (enStaging is null) continue;

                // Se vuelve a homologar para trabajar con datos frescos: la fecha
                // de contabilizacion, el correlativo y el cierre contable pudieron
                // cambiar desde que se homologo la primera vez.
                var resultado = await _homologacion.HomologarAsync(enStaging.Informe, enStaging.Gastos, ct);
                if (resultado.MotivoEspera is not null)
                {
                    // Vuelve a estado 0: por ejemplo un informe que se homologo
                    // estando abierto antes de que existiera esta regla.
                    await _staging.MarcarEnEsperaAsync(id, resultado.MotivoEspera, resultado.DocumentoEmpleado, ct);
                    continue;
                }
                if (!resultado.Ok)
                {
                    var mensaje = string.Join(" | ", resultado.Errores);
                    await _staging.RegistrarErrorAsync(id, mensaje, _opciones.MaxIntentos,
                                                       resultado.DocumentoEmpleado, ct);
                    continue;
                }

                var inf = resultado.Informe!;
                var lineas = _asiento.Construir(inf);
                var res = await _comprobantes.ContabilizarInformeAsync(
                    inf, lineas, _opciones.CodUsuarioErp, ct);

                await _staging.MarcarContabilizadoAsync(id, res.CodComprobante, res.FolioComprobante, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo la contabilizacion del informe {Id}", id);
                await _staging.RegistrarErrorAsync(id, ex.Message, _opciones.MaxIntentos, null, ct);
            }
        }
    }
}
