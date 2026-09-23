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
    private readonly ServicioCicloRendicion _rendiciones;
    private readonly ServicioCicloFondo _fondos;
    private readonly ServicioCicloSolicitud _solicitudes;
    private readonly ServicioProveedor _proveedores;
    private readonly OpcionesIntegracion _opciones;
    private readonly ILogger<ServicioCiclo> _logger;

    public ServicioCiclo(
        ClienteRindegastos api,
        RepositorioStaging staging,
        RepositorioComprobante comprobantes,
        ServicioHomologacion homologacion,
        ServicioAsiento asiento,
        ServicioCicloRendicion rendiciones,
        ServicioCicloFondo fondos,
        ServicioCicloSolicitud solicitudes,
        ServicioProveedor proveedores,
        IOptions<OpcionesIntegracion> opciones,
        ILogger<ServicioCiclo> logger)
    {
        _api = api;
        _staging = staging;
        _comprobantes = comprobantes;
        _homologacion = homologacion;
        _asiento = asiento;
        _rendiciones = rendiciones;
        _fondos = fondos;
        _solicitudes = solicitudes;
        _proveedores = proveedores;
        _opciones = opciones.Value;
        _logger = logger;
    }

    public async Task EjecutarAsync(CancellationToken ct)
    {
        _logger.LogInformation("===== Inicio de ciclo (SoloLectura = {Modo}) =====", _opciones.SoloLectura);

        // --- Flujo 1: gastos con documento -> factura de compra + comprobante ---
        _logger.LogInformation("--- Gastos con documento (facturas, boletas, recibos por honorarios) ---");

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

        // --- Flujo 2: informes cerrados -> Ingreso de Comprobante ----------------
        // Va aparte porque la unidad de trabajo es el informe, no el gasto: un
        // informe con varios gastos produce un solo comprobante. Corre DESPUES
        // del flujo 1 porque cancela los documentos que este acaba de registrar.
        if (_opciones.ProcesarRendiciones)
        {
            _logger.LogInformation("--- Informes cerrados (rendiciones) -> Ingreso de Comprobante ---");
            await _rendiciones.EjecutarAsync(ct);
        }

        // --- Flujo 3: entrega de fondos -> comprobante de transferencia ----------
        if (_opciones.ProcesarFondos)
        {
            _logger.LogInformation("--- Entrega de fondos (cajas chicas) -> Ingreso de Comprobante ---");
            await _fondos.EjecutarAsync(ct);
        }

        // --- Flujo 4: solicitudes de fondo aprobadas -> transferencia ------------
        if (_opciones.ProcesarSolicitudesFondo)
        {
            _logger.LogInformation("--- Solicitudes de fondo aprobadas -> Ingreso de Comprobante ---");
            await _solicitudes.EjecutarAsync(ct);
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

        // Las planillas de movilidad no se contabilizan aqui: van por el flujo de
        // rendiciones, agrupadas por informe. Si entraran por este flujo fallarian
        // siempre, porque el tipo de documento "PL" no tiene equivalente en
        // ref_tipo_documento_contable ni existe como documento de compra.
        var movilidad = gastos.Count(g => g.EsPlanillaMovilidad);
        var deCompra = gastos.Where(g => !g.EsPlanillaMovilidad).ToList();

        // Si el Id ya esta en staging, se ignora. Es la garantia anti duplicados.
        var existentes = await _staging.ObtenerIdsExistentesAsync(deCompra.Select(g => g.Id), ct);
        var nuevos = deCompra.Where(g => !existentes.Contains(g.Id)).ToList();

        _logger.LogInformation(
            "Descargados {Total} gastos: {Nuevos} nuevos, {Repetidos} ya conocidos, " +
            "{Movilidad} de movilidad (van por el flujo de rendiciones)",
            gastos.Count, nuevos.Count, deCompra.Count - nuevos.Count, movilidad);

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

        // Los ya conocidos que todavia no se contabilizaron se actualizan con lo
        // que acaba de mandar la API. Si alguien corrigio el gasto en Rindegastos,
        // vuelve a pendiente y se procesa con los datos nuevos. No hay riesgo de
        // duplicar: solo toca los estados 0, 1 y 9, y la API solo devuelve gastos
        // que aun no estan integrados.
        var conocidos = deCompra.Where(g => existentes.Contains(g.Id)).ToList();
        if (conocidos.Count > 0)
        {
            try
            {
                var cambiados = await _staging.RefrescarPendientesAsync(conocidos, ct);
                if (cambiados > 0)
                    _logger.LogInformation(
                        "{N} gastos pendientes cambiaron en Rindegastos y vuelven a procesarse", cambiados);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudieron actualizar los gastos pendientes en staging");
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

            // Si el proveedor no existe, se da de alta antes de homologar
            // (solo si SUNAT lo reporta ACTIVO y HABIDO). Si no se puede, el
            // motivo queda como error del gasto y no se sigue.
            string? motivoProveedor;
            try
            {
                motivoProveedor = await _proveedores.AsegurarAsync(gasto, _opciones.SoloLectura, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo el alta del proveedor del gasto {Id}", id);
                motivoProveedor = "No se pudo dar de alta el proveedor: " + ex.Message;
            }

            if (motivoProveedor is not null)
            {
                _logger.LogWarning("Gasto {Id}: {Motivo}", id, motivoProveedor);
                await _staging.RegistrarErrorAsync(id, motivoProveedor, _opciones.MaxIntentos, ct);
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
