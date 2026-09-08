using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rindegastos.Worker.Configuracion;

namespace Rindegastos.Worker.Datos;

/// <summary>Abre conexiones a la base de datos del ERP.</summary>
public sealed class FabricaConexion
{
    private readonly string _cadena;

    public FabricaConexion(IOptions<OpcionesBaseDatos> opciones)
        => _cadena = opciones.Value.CadenaConexion;

    public async Task<SqlConnection> AbrirAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_cadena);
        await cn.OpenAsync(ct);
        return cn;
    }
}

/// <summary>Escribe la bitacora de llamadas a la API en rg_log_api.</summary>
public sealed class RepositorioLog
{
    private readonly FabricaConexion _fabrica;
    private readonly ILogger<RepositorioLog> _logger;

    public RepositorioLog(FabricaConexion fabrica, ILogger<RepositorioLog> logger)
    {
        _fabrica = fabrica;
        _logger = logger;
    }

    public async Task RegistrarAsync(string metodo, int? httpStatus, int ms, bool ok,
                                     string? request, string? response, CancellationToken ct)
    {
        try
        {
            await using var cn = await _fabrica.AbrirAsync(ct);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.rg_log_api (rgl_metodo, rgl_http_status, rgl_ms, rgl_ok, rgl_request, rgl_response)
VALUES (@metodo, @status, @ms, @ok, @req, @resp);";
            cmd.Parameters.AddWithValue("@metodo", metodo);
            cmd.Parameters.AddWithValue("@status", (object?)httpStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", ms);
            cmd.Parameters.AddWithValue("@ok", ok);
            cmd.Parameters.AddWithValue("@req", (object?)request ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@resp", (object?)response ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // El log nunca debe tumbar el proceso principal.
            _logger.LogWarning(ex, "No se pudo escribir en rg_log_api");
        }
    }
}
