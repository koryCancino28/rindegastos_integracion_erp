using Microsoft.Data.SqlClient;

namespace Rindegastos.Worker.Datos;

/// <summary>Datos de una cuenta contable y sus banderas de configuracion.</summary>
public sealed record CuentaContable(
    int CodPlanCuenta,
    string CodigoCuenta,
    string Nombre,
    bool RequiereAnalisis,
    bool RequiereCentroCosto,
    bool RequiereItemGasto,
    bool RequiereTipoDocumento,
    bool RequiereNumeroDocumento,
    bool RequiereFechaVencimiento,
    bool Vigente);

/// <summary>
/// Consultas de solo lectura a los catalogos del ERP.
/// Replican exactamente las busquedas que hace la pantalla wctrFacturaCompra2.ascx.
/// </summary>
public sealed class RepositorioCatalogo
{
    private readonly FabricaConexion _fabrica;

    public RepositorioCatalogo(FabricaConexion fabrica) => _fabrica = fabrica;

    /// <summary>
    /// Busca el proveedor por RUC. Equivale a clsProveedor.mtdCargaDatosProveedorPorRuc.
    /// Devuelve (codigo, razon social) o null si no existe.
    /// </summary>
    public async Task<(int Cod, string Nombre)?> BuscarProveedorPorRucAsync(string ruc, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 mpr_cod_proveedor, mpr_nombre
            FROM dbo.mae_proveedor
            WHERE LTRIM(RTRIM(mpr_id)) = @ruc;";
        cmd.Parameters.AddWithValue("@ruc", ruc.Trim());

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.GetInt32(0), rd.GetString(1).Trim());
    }

    /// <summary>
    /// Busca el analisis contable del proveedor.
    /// Replica clsADAnalisis.mtdSelectBuscaAnalisisProveedor: el analisis se une
    /// con el proveedor por el RUC, limpiando separadores.
    /// </summary>
    public async Task<long?> BuscarAnalisisProveedorAsync(int codProveedor, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 ta.tan_cod_analisis
            FROM dbo.mae_proveedor p
            INNER JOIN dbo.tran_analisis ta
                ON ta.tan_cod_interno_analisis = CAST(
                    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        p.mpr_id,'/',''),'.',''),'-',''),'k','0'),'x','0'),'t',''),'f','') AS BIGINT)
            WHERE p.mpr_cod_proveedor = @cod;";
        cmd.Parameters.AddWithValue("@cod", codProveedor);

        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt64(r);
    }

    /// <summary>
    /// Traduce el codigo SUNAT que manda Rindegastos al codigo interno del ERP.
    /// Primero consulta rg_homologacion; si no hay fila, busca directo por rtdc_cod_sunat.
    /// </summary>
    public async Task<short?> BuscarTipoDocumentoAsync(string codigoRindegastos, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);

        await using (var cmdH = cn.CreateCommand())
        {
            cmdH.CommandText = @"
                SELECT TOP 1 rgh_valor_erp FROM dbo.rg_homologacion
                WHERE rgh_tipo = 'TIPO_DOCUMENTO' AND rgh_valor_rg = @cod AND rgh_vigente = 1;";
            cmdH.Parameters.AddWithValue("@cod", codigoRindegastos.Trim());
            var h = await cmdH.ExecuteScalarAsync(ct) as string;
            if (short.TryParse(h, out var codHomologado)) return codHomologado;
        }

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 rtdc_cod_tipo_documento_contable
            FROM dbo.ref_tipo_documento_contable
            WHERE LTRIM(RTRIM(rtdc_cod_sunat)) = @cod AND rtdc_vigente = 'S';";
        cmd.Parameters.AddWithValue("@cod", codigoRindegastos.Trim());
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt16(r);
    }

    /// <summary>Traduce el codigo ISO 4217 al codigo de moneda del ERP (ref_moneda.rmo_iso).</summary>
    public async Task<byte?> BuscarMonedaAsync(string iso, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 rmo_cod_moneda FROM dbo.ref_moneda
            WHERE LTRIM(RTRIM(rmo_iso)) = @iso;";
        cmd.Parameters.AddWithValue("@iso", iso.Trim().ToUpperInvariant());
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToByte(r);
    }

    /// <summary>
    /// Tipo de cambio vigente a una fecha.
    /// Replica clsADTipoCambio.mtdSelectTipoCambioVigenteFecha (usa ttc_valor_cambio, venta).
    /// Para soles siempre devuelve 1.
    /// </summary>
    public async Task<decimal?> BuscarTipoCambioAsync(byte codMoneda, DateTime fecha, CancellationToken ct)
    {
        if (codMoneda == Modelo.ConstantesErp.MonedaSoles) return 1m;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 ttc_valor_cambio
            FROM dbo.tran_tipo_cambio
            WHERE ttc_cod_moneda = 2 AND ttc_fecha_inicio_vigencia <= @fecha
            ORDER BY ttc_fecha_inicio_vigencia DESC;";
        cmd.Parameters.AddWithValue("@fecha", fecha.Date);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToDecimal(r);
    }

    /// <summary>Busca la cuenta contable por su codigo (mpc_codigo_cuenta).</summary>
    public async Task<CuentaContable?> BuscarCuentaAsync(string codigoCuenta, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 mpc_cod_plan_cuenta, mpc_codigo_cuenta, mpc_nombre,
                   mpc_requiere_analisis, mpc_requiere_centro_costo, mpc_requiere_item_gasto,
                   mpc_requiere_tipo_documento, mpc_requiere_numero_documento,
                   mpc_requiere_fecha_vencimiento, mpc_cuenta_vigente
            FROM dbo.mae_plan_cuenta
            WHERE LTRIM(RTRIM(mpc_codigo_cuenta)) = @cuenta;";
        cmd.Parameters.AddWithValue("@cuenta", codigoCuenta.Trim());

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        return new CuentaContable(
            rd.GetInt32(0), rd.GetString(1).Trim(), rd.GetString(2).Trim(),
            rd.GetBoolean(3), rd.GetBoolean(4), rd.GetBoolean(5),
            rd.GetBoolean(6), rd.GetBoolean(7), rd.GetBoolean(8), rd.GetBoolean(9));
    }

    /// <summary>Verifica que el centro de costo exista.</summary>
    public async Task<bool> ExisteCentroCostoAsync(short cod, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.ref_centro_costo WHERE rcc_cod_centro_costo = @cod;";
        cmd.Parameters.AddWithValue("@cod", cod);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Tasa de IGV vigente. Replica clsParametroCalculo con
    /// ConstCodTipoParametroIva = 1 sobre ref_parametro_general_calculo.
    /// </summary>
    public async Task<decimal> ObtenerTasaIgvAsync(CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT rpg_valor_parametro FROM dbo.ref_parametro_general_calculo
            WHERE rpg_cod_parametro_general_calculo = 1;";
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? 18m : Convert.ToDecimal(r);
    }

    /// <summary>
    /// Fecha del ultimo cierre contable (tipo 1).
    /// El ERP no permite grabar facturas con fecha menor o igual a esta.
    /// </summary>
    public async Task<DateTime?> ObtenerFechaCierreContableAsync(CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 tcm_fecha FROM dbo.tran_cierre_mensual
            WHERE tcm_cod_tipo_cierre = 1 ORDER BY tcm_fecha DESC;";
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToDateTime(r);
    }

    /// <summary>
    /// Verifica si la factura ya existe en el ERP.
    /// Es la misma llave natural que usa clsFacturaBoleta.mtdCargarFacturaCompra:
    /// folio + proveedor + tipo de documento.
    /// </summary>
    public async Task<int?> BuscarFacturaExistenteAsync(
        string folio, int codProveedor, short codTipoDoc, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 mfb_cod_factura_boleta
            FROM dbo.mae_factura_boleta
            WHERE LTRIM(RTRIM(mfb_folio)) = @folio
              AND mfb_cod_proveedor = @prov
              AND mfb_cod_tipo_factura_boleta = @tipo;";
        cmd.Parameters.AddWithValue("@folio", folio.Trim());
        cmd.Parameters.AddWithValue("@prov", codProveedor);
        cmd.Parameters.AddWithValue("@tipo", codTipoDoc);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }
}
