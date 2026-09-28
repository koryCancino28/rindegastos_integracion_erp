using System.Text.Json;
using Microsoft.Data.SqlClient;
using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Datos;

/// <summary>Un informe guardado en staging, con sus gastos.</summary>
public sealed record InformeEnStaging(InformeApi Informe, List<GastoApi> Gastos);

/// <summary>
/// Guarda y consulta los informes de rendicion en la tabla rg_informe.
/// Es el equivalente de RepositorioStaging, pero la unidad de trabajo aqui es
/// el informe completo (con todos sus gastos), no el gasto suelto.
/// </summary>
public sealed class RepositorioStagingInforme
{
    private readonly FabricaConexion _fabrica;
    private readonly ILogger<RepositorioStagingInforme> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public RepositorioStagingInforme(FabricaConexion fabrica, ILogger<RepositorioStagingInforme> logger)
    {
        _fabrica = fabrica;
        _logger = logger;
    }

    /// <summary>Ids que ya estan en staging. Evita volver a procesar lo conocido.</summary>
    public async Task<HashSet<long>> ObtenerIdsExistentesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var lista = ids.ToList();
        var encontrados = new HashSet<long>();
        if (lista.Count == 0) return encontrados;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgi_id FROM dbo.rg_informe WHERE rgi_id IN (" +
                          string.Join(",", lista.Select((_, i) => "@p" + i)) + ");";
        for (var i = 0; i < lista.Count; i++) cmd.Parameters.AddWithValue("@p" + i, lista[i]);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) encontrados.Add(rd.GetInt64(0));
        return encontrados;
    }

    /// <summary>Guarda el informe y sus gastos tal como llegaron de la API.</summary>
    public async Task InsertarAsync(InformeApi informe, List<GastoApi> gastos, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.rg_informe
    (rgi_id, rgi_titulo, rgi_numero_informe, rgi_fecha_envio,
     rgi_empleado_id, rgi_empleado_nombre, rgi_tipo_rendicion, rgi_centro_costo_code,
     rgi_fondo_id, rgi_fondo_nombre, rgi_total, rgi_moneda, rgi_cantidad_gastos,
     rgi_json, rgi_json_gastos, rgi_estado)
VALUES
    (@id, @titulo, @numero, @envio,
     @empId, @empNombre, @tipoRend, @ccCode,
     @fondoId, @fondoNombre, @total, @moneda, @cantidad,
     @json, @jsonGastos, @estado);";

        cmd.Parameters.AddWithValue("@id", informe.Id);
        cmd.Parameters.AddWithValue("@titulo", Texto(informe.Title, 300));
        cmd.Parameters.AddWithValue("@numero", Texto(informe.ReportNumber, 30));
        cmd.Parameters.AddWithValue("@envio", (object?)informe.SentDate?.Date ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@empId", (object?)informe.Employee?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@empNombre", Texto(informe.Employee?.Name, 200));
        cmd.Parameters.AddWithValue("@tipoRend", Texto(informe.TipoRendicionTexto, 100));
        cmd.Parameters.AddWithValue("@ccCode", Texto(informe.CampoExtra("Centro de Costos")?.Code, 20));
        cmd.Parameters.AddWithValue("@fondoId", (object?)informe.FundId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fondoNombre", Texto(informe.FundName, 200));
        cmd.Parameters.AddWithValue("@total", informe.ReportTotal);
        cmd.Parameters.AddWithValue("@moneda", Texto(informe.Currency, 10));
        cmd.Parameters.AddWithValue("@cantidad", informe.NumberExpenses);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(informe));
        cmd.Parameters.AddWithValue("@jsonGastos", JsonSerializer.Serialize(gastos));
        cmd.Parameters.AddWithValue("@estado", EstadoInforme.Descargado);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// De los ids dados, los que estan en staging y todavia no tienen comprobante
    /// (estado 0, 1 o 9). Son los que vale la pena refrescar con lo ultimo de la API.
    /// </summary>
    public async Task<HashSet<long>> ObtenerIdsSinComprobanteAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var lista = ids.ToList();
        var encontrados = new HashSet<long>();
        if (lista.Count == 0) return encontrados;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgi_id FROM dbo.rg_informe WHERE rgi_estado IN (0, 1, 9) AND rgi_id IN (" +
                          string.Join(",", lista.Select((_, i) => "@p" + i)) + ");";
        for (var i = 0; i < lista.Count; i++) cmd.Parameters.AddWithValue("@p" + i, lista[i]);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) encontrados.Add(rd.GetInt64(0));
        return encontrados;
    }

    /// <summary>
    /// Actualiza un informe que ya estaba en staging con lo que devuelve hoy la
    /// API. Pasa sobre todo cuando se descargo abierto y despues se cerro, o
    /// cuando corrigieron algo en Rindegastos. Si cambio, vuelve a estado 0 con
    /// los reintentos en cero. Nunca toca uno que ya tiene comprobante.
    /// Devuelve true si hubo cambios.
    /// </summary>
    public async Task<bool> RefrescarAsync(InformeApi informe, List<GastoApi> gastos, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_informe SET
    rgi_titulo            = @titulo,
    rgi_tipo_rendicion    = @tipoRend,
    rgi_centro_costo_code = @ccCode,
    rgi_total             = @total,
    rgi_moneda            = @moneda,
    rgi_cantidad_gastos   = @cantidad,
    rgi_json              = @json,
    rgi_json_gastos       = @jsonGastos,
    rgi_estado            = @estado,
    rgi_intentos          = 0,
    rgi_ultimo_error      = NULL
WHERE rgi_id = @id
  AND rgi_estado IN (0, 1, 9)
  AND (rgi_json IS NULL OR rgi_json <> @json
       OR rgi_json_gastos IS NULL OR rgi_json_gastos <> @jsonGastos);";

        cmd.Parameters.AddWithValue("@id", informe.Id);
        cmd.Parameters.AddWithValue("@titulo", Texto(informe.Title, 300));
        cmd.Parameters.AddWithValue("@tipoRend", Texto(informe.TipoRendicionTexto, 100));
        cmd.Parameters.AddWithValue("@ccCode", Texto(informe.CampoExtra("Centro de Costos")?.Code, 20));
        cmd.Parameters.AddWithValue("@total", informe.ReportTotal);
        cmd.Parameters.AddWithValue("@moneda", Texto(informe.Currency, 10));
        cmd.Parameters.AddWithValue("@cantidad", informe.NumberExpenses);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(informe));
        cmd.Parameters.AddWithValue("@jsonGastos", JsonSerializer.Serialize(gastos));
        cmd.Parameters.AddWithValue("@estado", EstadoInforme.Descargado);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Deja el informe esperando (estado 0) con el motivo a la vista, SIN sumar
    /// un reintento: no es un error, falta que pase algo afuera (que se cierre el
    /// informe o que sus documentos entren por compras).
    /// </summary>
    public async Task MarcarEnEsperaAsync(long id, string motivo, string? documentoEmpleado, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_informe SET
    rgi_estado             = @estado,
    rgi_ultimo_error       = @motivo,
    rgi_documento_empleado = COALESCE(@documento, rgi_documento_empleado)
WHERE rgi_id = @id AND rgi_estado IN (0, 1);";
        cmd.Parameters.AddWithValue("@estado", EstadoInforme.Descargado);
        cmd.Parameters.AddWithValue("@motivo", "EN ESPERA: " + (motivo.Length > 1980 ? motivo[..1980] : motivo));
        cmd.Parameters.AddWithValue("@documento", Texto(documentoEmpleado, 20));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ids de los informes que estan en un estado dado.</summary>
    public async Task<List<long>> ObtenerIdsPorEstadoAsync(byte estado, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgi_id FROM dbo.rg_informe WHERE rgi_estado = @e ORDER BY rgi_id;";
        cmd.Parameters.AddWithValue("@e", estado);

        var ids = new List<long>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetInt64(0));
        return ids;
    }

    /// <summary>Recupera el informe y sus gastos desde el JSON guardado.</summary>
    public async Task<InformeEnStaging?> ObtenerAsync(long id, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgi_json, rgi_json_gastos FROM dbo.rg_informe WHERE rgi_id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        if (rd.IsDBNull(0)) return null;

        try
        {
            var informe = JsonSerializer.Deserialize<InformeApi>(rd.GetString(0), JsonOpts);
            var gastos = rd.IsDBNull(1)
                ? new List<GastoApi>()
                : JsonSerializer.Deserialize<List<GastoApi>>(rd.GetString(1), JsonOpts) ?? new();

            return informe is null ? null : new InformeEnStaging(informe, gastos);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "El JSON guardado del informe {Id} no se pudo leer", id);
            return null;
        }
    }

    /// <summary>Guarda lo que resolvio la homologacion y pasa el informe a estado 1.</summary>
    public async Task GuardarHomologacionAsync(InformeHomologado inf, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_informe SET
    rgi_cod_analisis         = @analisis,
    rgi_documento_empleado   = @documento,
    rgi_tipo_comprobante     = @tipoComp,
    rgi_cuenta_contrapartida = @cuenta,
    rgi_numero_documento     = @numdoc,
    rgi_fecha_vencimiento    = @vence,
    rgi_estado               = @estado,
    rgi_ultimo_error         = NULL
WHERE rgi_id = @id;";

        cmd.Parameters.AddWithValue("@analisis", inf.CodAnalisisEmpleado);
        cmd.Parameters.AddWithValue("@documento", Texto(inf.DocumentoEmpleado, 20));
        cmd.Parameters.AddWithValue("@tipoComp", inf.Regla.TipoComprobante);
        // La cuenta realmente elegida, MN o ME segun la moneda de los gastos.
        cmd.Parameters.AddWithValue("@cuenta", Texto(inf.CuentaContrapartida, 20));
        cmd.Parameters.AddWithValue("@numdoc", Texto(inf.NumeroDocumentoContrapartida, 20));
        cmd.Parameters.AddWithValue("@vence", inf.FechaVencimientoContrapartida.Date);
        cmd.Parameters.AddWithValue("@estado", EstadoInforme.Homologado);
        cmd.Parameters.AddWithValue("@id", inf.IdRindegastos);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deja constancia del comprobante creado y pasa el informe a estado 2.</summary>
    public async Task MarcarContabilizadoAsync(
        long id, int codComprobante, int folioComprobante, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_informe SET
    rgi_cod_comprobante     = @comp,
    rgi_folio_comprobante   = @folio,
    rgi_estado              = @estado,
    rgi_fecha_contabilizado = GETDATE(),
    rgi_ultimo_error        = NULL
WHERE rgi_id = @id;";
        cmd.Parameters.AddWithValue("@comp", codComprobante);
        cmd.Parameters.AddWithValue("@folio", folioComprobante);
        cmd.Parameters.AddWithValue("@estado", EstadoInforme.Contabilizado);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Informes contabilizados que todavia no fueron marcados en Rindegastos.
    /// Devuelve la marca del informe y los ids de sus PLANILLAS DE MOVILIDAD,
    /// porque hay que marcar las dos cosas: el informe y cada planilla.
    ///
    /// Las facturas, boletas y RxH no se marcan aqui: ya las marco el flujo de
    /// compras con el codigo de SU comprobante de compra, y pisarlo con el del
    /// informe haria perder la referencia al documento.
    /// </summary>
    public async Task<List<(MarcaIntegracionInforme Marca, List<long> IdsGastos)>>
        ObtenerPendientesDeConfirmarAsync(CancellationToken ct)
    {
        var pendientes = new List<(MarcaIntegracionInforme, List<long>)>();

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT rgi_id, rgi_cod_comprobante, rgi_fecha_contabilizado, rgi_json_gastos
FROM dbo.rg_informe
WHERE rgi_estado = @estado AND rgi_cod_comprobante IS NOT NULL;";
        cmd.Parameters.AddWithValue("@estado", EstadoInforme.Contabilizado);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var fecha = rd.IsDBNull(2) ? DateTime.Today : rd.GetDateTime(2);

            var idsGastos = new List<long>();
            if (!rd.IsDBNull(3))
            {
                try
                {
                    var gastos = JsonSerializer.Deserialize<List<GastoApi>>(rd.GetString(3), JsonOpts);
                    if (gastos is not null)
                        idsGastos.AddRange(gastos
                            .Where(g => g.EsPlanillaMovilidad && g.Status != ConstantesRendicion.GastoRechazado)
                            .Select(g => g.Id));
                }
                catch (JsonException) { /* se marca al menos el informe */ }
            }

            pendientes.Add((new MarcaIntegracionInforme
            {
                Id = rd.GetInt64(0),
                IntegrationStatus = ConstantesMarcaIntegracion.Integrado,
                IntegrationCode = rd.GetInt32(1).ToString(),
                IntegrationDate = fecha.ToString("yyyy-MM-dd")
            }, idsGastos));
        }

        return pendientes;
    }

    /// <summary>Pasa los informes a estado 3 despues de confirmarlos en Rindegastos.</summary>
    public async Task MarcarConfirmadoAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var lista = ids.ToList();
        if (lista.Count == 0) return;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.rg_informe SET rgi_estado = @estado, rgi_fecha_confirmado = GETDATE() " +
            "WHERE rgi_id IN (" + string.Join(",", lista.Select((_, i) => "@p" + i)) + ");";
        cmd.Parameters.AddWithValue("@estado", EstadoInforme.Confirmado);
        for (var i = 0; i < lista.Count; i++) cmd.Parameters.AddWithValue("@p" + i, lista[i]);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Anota el error. Al pasar de maxIntentos el informe queda en estado 9 para
    /// que alguien lo revise, en vez de reintentar sin fin en cada ciclo.
    /// </summary>
    /// <param name="documentoEmpleado">
    /// Documento de quien rinde, si se llego a leer. Se guarda aunque falle para
    /// que la consulta L3 diga a quien le falta el analisis.
    /// </param>
    public async Task RegistrarErrorAsync(
        long id, string mensaje, int maxIntentos, string? documentoEmpleado, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_informe SET
    rgi_intentos           = rgi_intentos + 1,
    rgi_ultimo_error       = @error,
    rgi_documento_empleado = COALESCE(@documento, rgi_documento_empleado),
    rgi_estado             = CASE WHEN rgi_intentos + 1 >= @max THEN @error_estado ELSE rgi_estado END
WHERE rgi_id = @id;";
        cmd.Parameters.AddWithValue("@error", mensaje.Length > 2000 ? mensaje[..2000] : mensaje);
        cmd.Parameters.AddWithValue("@documento", Texto(documentoEmpleado, 20));
        cmd.Parameters.AddWithValue("@max", maxIntentos);
        cmd.Parameters.AddWithValue("@error_estado", EstadoInforme.Error);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Recorta y convierte a DBNull, para no reventar por un campo largo.</summary>
    private static object Texto(string? valor, int max)
    {
        if (string.IsNullOrWhiteSpace(valor)) return DBNull.Value;
        var t = valor.Trim();
        return t.Length > max ? t[..max] : t;
    }
}
