using System.Data;
using Microsoft.Data.SqlClient;

namespace Rindegastos.Worker.Datos;

/// <summary>
/// Da de alta un proveedor en el ERP, igual que la pantalla
/// Abastecimiento > Mantenedor de Proveedores (wctrMantenedorProveedores2.ascx).
///
/// btnGrabar_Click, para un proveedor nuevo, hace en este orden:
///
///   1. sp_insert_mae_proveedor_1          mae_proveedor. El SP ademas llama a
///                                         SPU_API_PREPARA_DATOS_ANEXO_PARA_CONCAR,
///                                         que deja el anexo en mae_anexo_concar.
///   2. MAX(mpr_cod_proveedor)             recupera el codigo recien creado
///   3. guardarDetalleLeadTime()           NO se hace: con los campos vacios la
///                                         pantalla falla al convertirlos y no graba nada
///   4. sp_insert_nub_tipo_proveedor_1     un registro por cada tipo marcado; si el
///      usp_abs_actualiza_cod_centralizacion   tipo es 1 (Articulos) ademas llama a este
///   5. sp_insert_tran_analisis_1          el analisis contable (tabla 'PR'), solo si
///                                         no existe ya uno con ese mismo RUC
///
/// Verificado contra el proveedor 17024 (JULCA MENDOZA MIGUEL), creado a mano:
/// todos los campos opcionales en NULL, 1 fila en mae_anexo_concar, 1 analisis.
///
/// Todo va en una sola transaccion: o queda el proveedor completo con su
/// analisis, o no queda nada.
/// </summary>
public sealed class RepositorioProveedor
{
    /// <summary>ref_tipo_proveedor 1 = Articulos, 2 = Servicios.</summary>
    public const byte TipoArticulos = 1;
    public const byte TipoServicios = 2;

    /// <summary>
    /// Tipos que se marcan por defecto, como pidio Contabilidad
    /// (en la pantalla se seleccionan los dos con Ctrl).
    /// </summary>
    private static readonly byte[] TiposPorDefecto = { TipoArticulos, TipoServicios };

    /// <summary>Valores por defecto de la pantalla.</summary>
    private const bool ProveedorNacional = true;   // radNacionalSi viene marcado
    private const short FormaPagoEfectivo = 4;     // rdbEfectivo -> CodTipoFormaPago = 4
    private const short PaisPeru = 1;              // ref_pais 1 = PERU
    private const short MonedaSoles = 1;           // ref_moneda 1 = Soles
    private const bool Vigente = true;             // radVigenteSi viene marcado

    private readonly FabricaConexion _fabrica;
    private readonly ILogger<RepositorioProveedor> _logger;

    public RepositorioProveedor(FabricaConexion fabrica, ILogger<RepositorioProveedor> logger)
    {
        _fabrica = fabrica;
        _logger = logger;
    }

    /// <summary>
    /// Crea el proveedor y devuelve su codigo. Si mientras tanto alguien lo
    /// creo, no lo duplica: devuelve el codigo existente.
    /// </summary>
    /// <param name="ruc">RUC. Va en mpr_id y en mpr_NIF, igual que en la pantalla.</param>
    /// <param name="razonSocial">Va en nombre y en razon social.</param>
    public async Task<(int CodProveedor, bool Creado)> CrearAsync(
        string ruc, string razonSocial, CancellationToken ct)
    {
        ruc = ruc.Trim();
        var nombre = Recortar(razonSocial.Trim(), 50);            // mpr_nombre varchar(50)
        var razon = Recortar(razonSocial.Trim().ToUpperInvariant(), 50); // la pantalla la pasa a mayusculas

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            // Se vuelve a buscar dentro de la transaccion, con bloqueo, para no
            // crearlo dos veces si otro proceso lo acaba de dar de alta.
            var existente = await BuscarPorRucAsync(cn, tx, ruc, bloquear: true, ct);
            if (existente is not null)
            {
                await tx.CommitAsync(ct);
                return (existente.Value, false);
            }

            // ---- 1. mae_proveedor (y el anexo de CONCAR, que lo hace el mismo SP) --
            await using (var cmd = Sp(cn, tx, "sp_insert_mae_proveedor_1"))
            {
                cmd.Parameters.AddWithValue("@mpr_nombre_1", nombre);
                cmd.Parameters.AddWithValue("@mpr_razon_social_2", razon);
                cmd.Parameters.AddWithValue("@mpr_id_3", ruc);
                cmd.Parameters.AddWithValue("@mpr_proveedor_nacional_4", ProveedorNacional);
                cmd.Parameters.AddWithValue("@mpr_email_5", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_observaciones_6", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_meses_stock_seguridad_7", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_meses_stock_maximo_8", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_meses_transito_9", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_plazo_entrega_10", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_abreviatura_11", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_forma_pago_12", FormaPagoEfectivo);
                cmd.Parameters.AddWithValue("@mpr_cod_banco_13", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_cta_cte_14", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_cod_centralizacion_15", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_cod_pais_16", PaisPeru);
                cmd.Parameters.AddWithValue("@mpr_cod_moneda_17", MonedaSoles);
                cmd.Parameters.AddWithValue("@mpr_vigente_18", Vigente);
                cmd.Parameters.AddWithValue("@mpr_tiempo_revision_19", DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr_NIF_20", ruc);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // ---- 2. Codigo recien creado --------------------------------------------
            // La pantalla usa MAX(mpr_cod_proveedor) de toda la tabla; aqui se filtra
            // ademas por el RUC para no tomar por error el de otra alta simultanea.
            var codProveedor = await BuscarPorRucAsync(cn, tx, ruc, bloquear: false, ct)
                ?? throw new InvalidOperationException("No se pudo recuperar el codigo del proveedor recien creado.");

            // ---- 4. Tipos de proveedor ----------------------------------------------
            foreach (var tipo in TiposPorDefecto)
            {
                await using (var cmd = Sp(cn, tx, "sp_insert_nub_tipo_proveedor_1"))
                {
                    cmd.Parameters.AddWithValue("@ntp_cod_proveedor_1", codProveedor);
                    cmd.Parameters.AddWithValue("@ntp_cod_tipo_proveedor_2", tipo);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // Igual que la pantalla: al marcar Articulos se actualiza el codigo
                // de centralizacion. Hoy ningun proveedor tiene uno, asi que el SP
                // lo deja en NULL.
                if (tipo == TipoArticulos)
                {
                    await using var cmd = Sp(cn, tx, "usp_abs_actualiza_cod_centralizacion");
                    cmd.Parameters.AddWithValue("@CodProveedor", codProveedor);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            // ---- 5. Analisis contable -----------------------------------------------
            // Sin analisis no se puede grabar la factura: la linea del proveedor lo
            // exige. La pantalla lo crea solo si no existe ya uno con ese RUC.
            var codInterno = RucComoCodigoInterno(ruc);
            if (!await ExisteAnalisisAsync(cn, tx, codInterno, ct))
            {
                await using var cmd = Sp(cn, tx, "sp_insert_tran_analisis_1");
                cmd.Parameters.AddWithValue("@tan_cod_interno_analisis_1", codInterno);
                cmd.Parameters.AddWithValue("@tan_cod_item_asociado_2", codProveedor);
                cmd.Parameters.AddWithValue("@tan_tabla_asociada_3", "PR");
                cmd.Parameters.AddWithValue("@tan_nombre_4", Recortar(razonSocial.Trim(), 50));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            _logger.LogInformation("Proveedor {Ruc} {Nombre} creado en el ERP con codigo {Cod}",
                ruc, nombre, codProveedor);

            return (codProveedor, true);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<int?> BuscarPorRucAsync(
        SqlConnection cn, SqlTransaction tx, string ruc, bool bloquear, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = bloquear
            ? "SELECT MAX(mpr_cod_proveedor) FROM dbo.mae_proveedor WITH (UPDLOCK, HOLDLOCK) WHERE LTRIM(RTRIM(mpr_id)) = @ruc;"
            : "SELECT MAX(mpr_cod_proveedor) FROM dbo.mae_proveedor WHERE LTRIM(RTRIM(mpr_id)) = @ruc;";
        cmd.Parameters.AddWithValue("@ruc", ruc);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    private static async Task<bool> ExisteAnalisisAsync(
        SqlConnection cn, SqlTransaction tx, long codInterno, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM dbo.tran_analisis WHERE tan_cod_interno_analisis = @cod;";
        cmd.Parameters.AddWithValue("@cod", codInterno);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// El analisis guarda el RUC como numero, limpiando los mismos caracteres que
    /// la pantalla (puntos, guiones, barras, F, T, X; la K pasa a 0).
    /// </summary>
    public static long RucComoCodigoInterno(string ruc)
    {
        var limpio = ruc.Trim()
            .Replace(".", "").Replace("-", "").Replace("/", "")
            .Replace("K", "0").Replace("k", "0")
            .Replace("F", "").Replace("f", "").Replace("T", "").Replace("t", "")
            .Replace("X", "").Replace("x", "");
        return long.Parse(limpio);
    }

    private static SqlCommand Sp(SqlConnection cn, SqlTransaction tx, string nombre)
    {
        var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = nombre;
        return cmd;
    }

    private static string Recortar(string texto, int max) => texto.Length > max ? texto[..max] : texto;
}
