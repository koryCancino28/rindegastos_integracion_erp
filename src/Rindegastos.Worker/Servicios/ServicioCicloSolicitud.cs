using Microsoft.Extensions.Options;
using Rindegastos.Worker.Api;
using Rindegastos.Worker.Configuracion;
using Rindegastos.Worker.Datos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Servicios;

/// <summary>
/// Ciclo de las SOLICITUDES DE FONDO -> Ingreso de Comprobante (transferencia).
///
///   Paso 0  Reparacion     reintenta marcar en Rindegastos lo ya contabilizado
///   Paso 1  Descarga       trae las solicitudes aprobadas y no integradas
///   Paso 2  Homologacion   resuelve persona, cuentas y tipo de cambio
///   Paso 3  Contabilizacion crea el comprobante tipo 19 en el ERP
///   Paso 4  Confirmacion   marca la solicitud en Rindegastos
///
/// Al aprobarse, Rindegastos crea ademas un fondo con el dinero solicitado. Ese
/// fondo NO se registra por el flujo de fondos (se reconoce porque trae
/// FundRequestId): la plata se entrega una sola vez y la registra este flujo.
/// </summary>
public sealed class ServicioCicloSolicitud
{
    private readonly ClienteRindegastos _api;
    private readonly RepositorioStagingSolicitud _staging;
    private readonly RepositorioComprobante _comprobantes;
    private readonly ServicioHomologacionSolicitud _homologacion;
    private readonly ServicioAsientoFondo _asiento;
    private readonly OpcionesIntegracion _opciones;
    private readonly ILogger<ServicioCicloSolicitud> _logger;

    public ServicioCicloSolicitud(
        ClienteRindegastos api,
        RepositorioStagingSolicitud staging,
        RepositorioComprobante comprobantes,
        ServicioHomologacionSolicitud homologacion,
        ServicioAsientoFondo asiento,
        IOptions<OpcionesIntegracion> opciones,
        ILogger<ServicioCicloSolicitud> logger)
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

        _logger.LogInformation("Marcando {N} solicitudes ya contabilizadas en Rindegastos", marcas.Count);

        try
        {
            await _api.MarcarSolicitudesFondoIntegradasAsync(marcas, ct);
            await _staging.MarcarConfirmadoAsync(marcas.Select(m => m.FundRequestId), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo la confirmacion de solicitudes en Rindegastos. " +
                                 "Se reintentara en el proximo ciclo.");
        }
    }

    // ------------------------------------------------------------------ Paso 1

    private async Task DescargarAsync(CancellationToken ct)
    {
        var solicitudes = await _api.ObtenerSolicitudesFondoAsync(ct);
        if (solicitudes.Count == 0)
        {
            _logger.LogInformation("No hay solicitudes de fondo por descargar.");
            return;
        }

        // Las que ya estan integradas en Rindegastos no vuelven a entrar.
        var candidatas = solicitudes.Where(s => !s.IsIntegration).ToList();

        DateTime? desde = DateTime.TryParse(_opciones.FechaDesde, out var d) ? d.Date : null;

        var existentes = await _staging.ObtenerIdsExistentesAsync(ct);
        var nuevas = 0;

        foreach (var s in candidatas)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || existentes.Contains(s.Id)) continue;

            // Una solicitud sin aprobar todavia puede cambiar: se toma en el ciclo
            // en que ya este aprobada.
            if (!s.Aprobada) continue;

            var fecha = s.ClosedDate ?? s.SentDate;
            if (desde is not null && fecha is not null
                && ConstantesFondo.FechaEnPeru(fecha.Value) < desde.Value)
                continue;

            try
            {
                await _staging.InsertarAsync(s, ct);
                nuevas++;

                _logger.LogInformation(
                    "Solicitud {Id} '{Titulo}' ({Politica}) guardada: {Monto:N2} {Moneda} para {Empleado}",
                    s.Id, s.Title, s.TipoRendicionTexto, s.Amount, s.Currency, s.EmployeeName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar en staging la solicitud {Id}", s.Id);
            }
        }

        _logger.LogInformation(
            "Solicitudes de fondo: {Total} recibidas, {Pendientes} sin integrar, {Nuevas} nuevas",
            solicitudes.Count, candidatas.Count, nuevas);
    }

    // ------------------------------------------------------------------ Paso 2

    private async Task HomologarAsync(CancellationToken ct)
    {
        var ids = await _staging.ObtenerIdsPorEstadoAsync(EstadoFondo.Descargado, ct);
        if (ids.Count == 0) return;

        _logger.LogInformation("Homologando {N} solicitudes de fondo", ids.Count);
        var ok = 0;

        foreach (var id in ids)
        {
            var solicitud = await _staging.ObtenerAsync(id, ct);
            if (solicitud is null)
            {
                await _staging.RegistrarErrorAsync(id, "No se pudo leer el JSON de la solicitud.",
                                                   _opciones.MaxIntentos, ct);
                continue;
            }

            var resultado = await _homologacion.HomologarAsync(solicitud, ct);
            if (resultado.Ok)
            {
                await _staging.GuardarHomologacionAsync(resultado.Fondo!, ct);
                ok++;
            }
            else
            {
                var mensaje = string.Join(" | ", resultado.Errores);
                _logger.LogWarning("Solicitud {Id} no homologada: {Errores}", id, mensaje);
                await _staging.RegistrarErrorAsync(id, mensaje, _opciones.MaxIntentos, ct);
            }
        }

        _logger.LogInformation("Homologacion de solicitudes terminada: {Ok} de {Total} correctas", ok, ids.Count);
    }

    // ------------------------------------------------------------------ Paso 3

    private async Task ContabilizarAsync(CancellationToken ct)
    {
        var ids = await _staging.ObtenerIdsPorEstadoAsync(EstadoFondo.Homologado, ct);
        if (ids.Count == 0) return;

        if (_opciones.CodUsuarioErp <= 0)
        {
            _logger.LogError("Integracion:CodUsuarioErp no esta configurado. No se contabiliza nada.");
            return;
        }

        _logger.LogInformation("Contabilizando {N} solicitudes de fondo", ids.Count);

        foreach (var id in ids)
        {
            try
            {
                var solicitud = await _staging.ObtenerAsync(id, ct);
                if (solicitud is null) continue;

                var resultado = await _homologacion.HomologarAsync(solicitud, ct);
                if (!resultado.Ok)
                {
                    await _staging.RegistrarErrorAsync(id, string.Join(" | ", resultado.Errores),
                                                       _opciones.MaxIntentos, ct);
                    continue;
                }

                var res = await _comprobantes.ContabilizarFondoAsync(
                    resultado.Fondo!, _asiento, _opciones.CodUsuarioErp, ct);

                await _staging.MarcarContabilizadoAsync(id, res.CodComprobante, res.FolioComprobante, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo la contabilizacion de la solicitud {Id}", id);
                await _staging.RegistrarErrorAsync(id, ex.Message, _opciones.MaxIntentos, ct);
            }
        }
    }
}
