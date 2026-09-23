using System.Text.Json;
using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Datos;

/// <summary>Un deposito de fondo guardado en staging, con el fondo completo.</summary>
public sealed record DepositoEnStaging(FondoApi Fondo, short Deposito, TransaccionFondoApi Transaccion);

/// <summary>
/// Guarda y consulta los depositos de fondos en la tabla rg_fondo.
/// La unidad es el deposito: un fondo con una entrega inicial y una recarga son
/// dos filas y dos comprobantes.
/// </summary>
public sealed class RepositorioStagingFondo
{
    private readonly FabricaConexion _fabrica;
    private readonly ILogger<RepositorioStagingFondo> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public RepositorioStagingFondo(FabricaConexion fabrica, ILogger<RepositorioStagingFondo> logger)
    {
        _fabrica = fabrica;
        _logger = logger;
    }

    /// <summary>Depositos de un fondo que ya estan guardados.</summary>
    public async Task<HashSet<short>> ObtenerDepositosExistentesAsync(long idFondo, CancellationToken ct)
    {
        var encontrados = new HashSet<short>();

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgf_deposito FROM dbo.rg_fondo WHERE rgf_id_fondo = @id;";
        cmd.Parameters.AddWithValue("@id", idFondo);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) encontrados.Add(Convert.ToInt16(rd.GetValue(0)));
        return encontrados;
    }

    /// <summary>Guarda un deposito tal como llego de la API.</summary>
    public async Task InsertarAsync(
        FondoApi fondo, short deposito, TransaccionFondoApi transaccion, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.rg_fondo
    (rgf_id_fondo, rgf_deposito, rgf_titulo, rgf_code, rgf_descripcion,
     rgf_moneda, rgf_monto, rgf_fecha_deposito, rgf_json, rgf_estado)
VALUES
    (@id, @dep, @titulo, @code, @descripcion,
     @moneda, @monto, @fecha, @json, @estado);";

        cmd.Parameters.AddWithValue("@id", fondo.Id);
        cmd.Parameters.AddWithValue("@dep", deposito);
        cmd.Parameters.AddWithValue("@titulo", Texto(fondo.Title, 300));
        cmd.Parameters.AddWithValue("@code", Texto(fondo.Code, 30));
        cmd.Parameters.AddWithValue("@descripcion", Texto(fondo.Description, 200));
        cmd.Parameters.AddWithValue("@moneda", Texto(transaccion.CurrencyCode ?? fondo.Currency, 10));
        cmd.Parameters.AddWithValue("@monto", transaccion.TransactionAmount);
        cmd.Parameters.AddWithValue("@fecha", (object?)transaccion.TransactionDate?.Date ?? DateTime.Today);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(fondo));
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Descargado);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Depositos que estan en un estado dado.</summary>
    public async Task<List<(long IdFondo, short Deposito)>> ObtenerPorEstadoAsync(
        byte estado, CancellationToken ct)
    {
        var lista = new List<(long, short)>();

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT rgf_id_fondo, rgf_deposito FROM dbo.rg_fondo
            WHERE rgf_estado = @e ORDER BY rgf_id_fondo, rgf_deposito;";
        cmd.Parameters.AddWithValue("@e", estado);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add((rd.GetInt64(0), Convert.ToInt16(rd.GetValue(1))));
        return lista;
    }

    /// <summary>Recupera el fondo y el deposito desde el JSON guardado.</summary>
    public async Task<DepositoEnStaging?> ObtenerAsync(long idFondo, short deposito, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgf_json FROM dbo.rg_fondo WHERE rgf_id_fondo = @id AND rgf_deposito = @dep;";
        cmd.Parameters.AddWithValue("@id", idFondo);
        cmd.Parameters.AddWithValue("@dep", deposito);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || rd.IsDBNull(0)) return null;

        try
        {
            var fondo = JsonSerializer.Deserialize<FondoApi>(rd.GetString(0), JsonOpts);
            if (fondo is null) return null;

            var depositos = fondo.Depositos;
            if (deposito < 1 || deposito > depositos.Count) return null;

            return new DepositoEnStaging(fondo, deposito, depositos[deposito - 1]);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "El JSON guardado del fondo {Id} no se pudo leer", idFondo);
            return null;
        }
    }

    /// <summary>Guarda lo que resolvio la homologacion y pasa el deposito a estado 1.</summary>
    public async Task GuardarHomologacionAsync(FondoHomologado f, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_fondo SET
    rgf_cuenta       = @cuenta,
    rgf_cod_analisis = @analisis,
    rgf_nombre_corto = @nombre,
    rgf_estado       = @estado,
    rgf_ultimo_error = NULL
WHERE rgf_id_fondo = @id AND rgf_deposito = @dep;";
        cmd.Parameters.AddWithValue("@cuenta", Texto(f.CuentaFondo, 20));
        cmd.Parameters.AddWithValue("@analisis", f.CodAnalisisEmpleado);
        cmd.Parameters.AddWithValue("@nombre", Texto(f.NombreCorto, 100));
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Homologado);
        cmd.Parameters.AddWithValue("@id", f.IdFondo);
        cmd.Parameters.AddWithValue("@dep", f.Deposito);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deja constancia del comprobante creado y pasa el deposito a estado 2.</summary>
    public async Task MarcarContabilizadoAsync(
        long idFondo, short deposito, int codComprobante, int folio, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_fondo SET
    rgf_cod_comprobante     = @comp,
    rgf_folio_comprobante   = @folio,
    rgf_estado              = @estado,
    rgf_fecha_contabilizado = GETDATE(),
    rgf_ultimo_error        = NULL
WHERE rgf_id_fondo = @id AND rgf_deposito = @dep;";
        cmd.Parameters.AddWithValue("@comp", codComprobante);
        cmd.Parameters.AddWithValue("@folio", folio);
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Contabilizado);
        cmd.Parameters.AddWithValue("@id", idFondo);
        cmd.Parameters.AddWithValue("@dep", deposito);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Fondos listos para marcar en Rindegastos: TODOS sus depositos guardados
    /// estan contabilizados. La marca es del fondo completo, asi que mientras
    /// quede un deposito sin comprobante no se marca nada.
    /// Devuelve el fondo con el comprobante y la fecha del ultimo deposito.
    /// </summary>
    public async Task<List<MarcaIntegracionFondo>> ObtenerPendientesDeConfirmarAsync(CancellationToken ct)
    {
        var marcas = new List<MarcaIntegracionFondo>();

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT f.rgf_id_fondo,
       MAX(f.rgf_cod_comprobante)      AS comprobante,
       MAX(f.rgf_fecha_contabilizado)  AS fecha
FROM dbo.rg_fondo f
WHERE f.rgf_estado = @contabilizado
  AND NOT EXISTS (SELECT 1 FROM dbo.rg_fondo o
                  WHERE o.rgf_id_fondo = f.rgf_id_fondo AND o.rgf_estado <> @contabilizado)
GROUP BY f.rgf_id_fondo;";
        cmd.Parameters.AddWithValue("@contabilizado", EstadoFondo.Contabilizado);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var fecha = rd.IsDBNull(2) ? DateTime.Today : rd.GetDateTime(2);
            marcas.Add(new MarcaIntegracionFondo
            {
                Id = rd.GetInt64(0),
                IntegrationStatus = 1,
                IntegrationCode = rd.GetInt32(1).ToString(),
                IntegrationDate = fecha.ToString("yyyy-MM-dd")
            });
        }

        return marcas;
    }

    /// <summary>Pasa todos los depositos de esos fondos a estado 3.</summary>
    public async Task MarcarConfirmadoAsync(IEnumerable<long> idsFondos, CancellationToken ct)
    {
        var lista = idsFondos.ToList();
        if (lista.Count == 0) return;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.rg_fondo SET rgf_estado = @estado, rgf_fecha_confirmado = GETDATE() " +
            "WHERE rgf_id_fondo IN (" + string.Join(",", lista.Select((_, i) => "@p" + i)) + ");";
        cmd.Parameters.AddWithValue("@estado", EstadoFondo.Confirmado);
        for (var i = 0; i < lista.Count; i++) cmd.Parameters.AddWithValue("@p" + i, lista[i]);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Anota el error. Al pasar de maxIntentos el deposito queda en estado 9.</summary>
    public async Task RegistrarErrorAsync(
        long idFondo, short deposito, string mensaje, int maxIntentos, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_fondo SET
    rgf_intentos     = rgf_intentos + 1,
    rgf_ultimo_error = @error,
    rgf_estado       = CASE WHEN rgf_intentos + 1 >= @max THEN @error_estado ELSE rgf_estado END
WHERE rgf_id_fondo = @id AND rgf_deposito = @dep;";
        cmd.Parameters.AddWithValue("@error", mensaje.Length > 2000 ? mensaje[..2000] : mensaje);
        cmd.Parameters.AddWithValue("@max", maxIntentos);
        cmd.Parameters.AddWithValue("@error_estado", EstadoFondo.Error);
        cmd.Parameters.AddWithValue("@id", idFondo);
        cmd.Parameters.AddWithValue("@dep", deposito);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object Texto(string? valor, int max)
    {
        if (string.IsNullOrWhiteSpace(valor)) return DBNull.Value;
        var t = valor.Trim();
        return t.Length > max ? t[..max] : t;
    }
}
