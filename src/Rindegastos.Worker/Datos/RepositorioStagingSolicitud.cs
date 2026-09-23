using System.Text.Json;
using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Datos;

/// <summary>
/// Guarda y consulta las solicitudes de fondo en la tabla rg_solicitud_fondo.
/// La unidad es la solicitud: cada una aprobada da un comprobante.
/// </summary>
public sealed class RepositorioStagingSolicitud
{
    private readonly FabricaConexion _fabrica;
    private readonly ILogger<RepositorioStagingSolicitud> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public RepositorioStagingSolicitud(FabricaConexion fabrica, ILogger<RepositorioStagingSolicitud> logger)
    {
        _fabrica = fabrica;
        _logger = logger;
    }

    /// <summary>Ids que ya estan en staging.</summary>
    public async Task<HashSet<string>> ObtenerIdsExistentesAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgs_id FROM dbo.rg_solicitud_fondo;";

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetString(0));
        return ids;
    }

    /// <summary>Guarda la solicitud tal como llego de la API.</summary>
    public async Task InsertarAsync(SolicitudFondoApi s, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.rg_solicitud_fondo
    (rgs_id, rgs_titulo, rgs_descripcion, rgs_politica, rgs_tipo_rendicion, rgs_dni,
     rgs_empleado_nombre, rgs_moneda, rgs_monto, rgs_fecha_aprobacion, rgs_id_fondo,
     rgs_json, rgs_estado)
VALUES
    (@id, @titulo, @desc, @politica, @tipo, @dni,
     @empleado, @moneda, @monto, @fecha, @fondo,
     @json, @estado);";

        var fecha = s.ClosedDate ?? s.SentDate;

        cmd.Parameters.AddWithValue("@id", s.Id);
        cmd.Parameters.AddWithValue("@titulo", Texto(s.Title, 300));
        cmd.Parameters.AddWithValue("@desc", Texto(s.Description, 300));
        cmd.Parameters.AddWithValue("@politica", Texto(s.PolicyName, 200));
        cmd.Parameters.AddWithValue("@tipo", Texto(s.TipoRendicionTexto, 100));
        cmd.Parameters.AddWithValue("@dni", Texto(s.Dni, 30));
        cmd.Parameters.AddWithValue("@empleado", Texto(s.EmployeeName, 200));
        cmd.Parameters.AddWithValue("@moneda", Texto(s.Currency, 10));
        cmd.Parameters.AddWithValue("@monto", s.Amount);
        cmd.Parameters.AddWithValue("@fecha",
            fecha is null ? DBNull.Value : ConstantesFondo.FechaEnPeru(fecha.Value));
        cmd.Parameters.AddWithValue("@fondo", (object?)s.FundId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(s));
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Descargado);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Solicitudes que estan en un estado dado.</summary>
    public async Task<List<string>> ObtenerIdsPorEstadoAsync(byte estado, CancellationToken ct)
    {
        var ids = new List<string>();

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgs_id FROM dbo.rg_solicitud_fondo WHERE rgs_estado = @e ORDER BY rgs_id;";
        cmd.Parameters.AddWithValue("@e", estado);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetString(0));
        return ids;
    }

    /// <summary>Recupera la solicitud desde el JSON guardado.</summary>
    public async Task<SolicitudFondoApi?> ObtenerAsync(string id, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgs_json FROM dbo.rg_solicitud_fondo WHERE rgs_id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || rd.IsDBNull(0)) return null;

        try
        {
            return JsonSerializer.Deserialize<SolicitudFondoApi>(rd.GetString(0), JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "El JSON guardado de la solicitud {Id} no se pudo leer", id);
            return null;
        }
    }

    /// <summary>Guarda lo que resolvio la homologacion y pasa la solicitud a estado 1.</summary>
    public async Task GuardarHomologacionAsync(FondoHomologado f, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_solicitud_fondo SET
    rgs_cuenta           = @cuenta,
    rgs_cod_analisis     = @analisis,
    rgs_nombre_corto     = @nombre,
    rgs_fecha_aprobacion = @fecha,
    rgs_estado           = @estado,
    rgs_ultimo_error     = NULL
WHERE rgs_id = @id;";
        cmd.Parameters.AddWithValue("@cuenta", Texto(f.CuentaFondo, 20));
        cmd.Parameters.AddWithValue("@analisis", f.CodAnalisisEmpleado);
        cmd.Parameters.AddWithValue("@nombre", Texto(f.NombreCorto, 100));
        cmd.Parameters.AddWithValue("@fecha", f.FechaDeposito.Date);
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Homologado);
        cmd.Parameters.AddWithValue("@id", f.IdSolicitud!);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deja constancia del comprobante creado y pasa la solicitud a estado 2.</summary>
    public async Task MarcarContabilizadoAsync(
        string id, int codComprobante, int folio, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_solicitud_fondo SET
    rgs_cod_comprobante     = @comp,
    rgs_folio_comprobante   = @folio,
    rgs_estado              = @estado,
    rgs_fecha_contabilizado = GETDATE(),
    rgs_ultimo_error        = NULL
WHERE rgs_id = @id;";
        cmd.Parameters.AddWithValue("@comp", codComprobante);
        cmd.Parameters.AddWithValue("@folio", folio);
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Contabilizado);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Solicitudes contabilizadas que todavia no se marcaron en Rindegastos.</summary>
    public async Task<List<MarcaIntegracionSolicitud>> ObtenerPendientesDeConfirmarAsync(CancellationToken ct)
    {
        var marcas = new List<MarcaIntegracionSolicitud>();

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT rgs_id, rgs_cod_comprobante, rgs_fecha_contabilizado
            FROM dbo.rg_solicitud_fondo
            WHERE rgs_estado = @estado AND rgs_cod_comprobante IS NOT NULL;";
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Contabilizado);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var fecha = rd.IsDBNull(2) ? DateTime.Today : rd.GetDateTime(2);
            marcas.Add(new MarcaIntegracionSolicitud
            {
                Id = rd.GetString(0),
                IntegrationStatus = 1,
                IntegrationCode = rd.GetInt32(1).ToString(),
                IntegrationDate = fecha.ToString("yyyy-MM-dd")
            });
        }

        return marcas;
    }

    /// <summary>Pasa las solicitudes a estado 3.</summary>
    public async Task MarcarConfirmadoAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var lista = ids.ToList();
        if (lista.Count == 0) return;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.rg_solicitud_fondo SET rgs_estado = @estado, rgs_fecha_confirmado = GETDATE() " +
            "WHERE rgs_id IN (" + string.Join(",", lista.Select((_, i) => "@p" + i)) + ");";
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Confirmado);
        for (var i = 0; i < lista.Count; i++) cmd.Parameters.AddWithValue("@p" + i, lista[i]);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Anota el error. Al pasar de maxIntentos la solicitud queda en estado 9.</summary>
    public async Task RegistrarErrorAsync(string id, string mensaje, int maxIntentos, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_solicitud_fondo SET
    rgs_intentos     = rgs_intentos + 1,
    rgs_ultimo_error = @error,
    rgs_estado       = CASE WHEN rgs_intentos + 1 >= @max THEN @error_estado ELSE rgs_estado END
WHERE rgs_id = @id;";
        cmd.Parameters.AddWithValue("@error", mensaje.Length > 2000 ? mensaje[..2000] : mensaje);
        cmd.Parameters.AddWithValue("@max", maxIntentos);
        cmd.Parameters.AddWithValue("@error_estado", EstadoFondo.Error);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object Texto(string? valor, int max)
    {
        if (string.IsNullOrWhiteSpace(valor)) return DBNull.Value;
        var t = valor.Trim();
        return t.Length > max ? t[..max] : t;
    }
}
