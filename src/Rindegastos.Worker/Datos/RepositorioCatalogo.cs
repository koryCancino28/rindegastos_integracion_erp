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

    // ------------------------------------------------- rendiciones (informes)

    /// <summary>
    /// Como quedo un gasto en el flujo de compras (rg_gasto), si el worker lo conoce.
    /// Sirve para saber si el documento ya se registro o por que no.
    /// </summary>
    public async Task<(byte Estado, int? CodFactura, string? Error)?> BuscarGastoEnComprasAsync(
        long idGasto, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT rgg_estado, rgg_cod_factura_boleta, rgg_ultimo_error
            FROM dbo.rg_gasto WHERE rgg_id = @id;";
        cmd.Parameters.AddWithValue("@id", idGasto);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (Convert.ToByte(rd.GetValue(0)),
                rd.IsDBNull(1) ? null : Convert.ToInt32(rd.GetValue(1)),
                rd.IsDBNull(2) ? null : rd.GetString(2));
    }

    /// <summary>
    /// Busca un documento de compra por RUC del proveedor, tipo y folio. Cubre los
    /// que Contabilidad registro a mano, que no pasan por rg_gasto.
    /// </summary>
    public async Task<int?> BuscarFacturaPorRucAsync(
        string ruc, short codTipoDoc, string folio, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 fb.mfb_cod_factura_boleta
            FROM dbo.mae_factura_boleta fb
            INNER JOIN dbo.mae_proveedor p ON p.mpr_cod_proveedor = fb.mfb_cod_proveedor
            WHERE LTRIM(RTRIM(fb.mfb_folio)) = @folio
              AND LTRIM(RTRIM(p.mpr_id)) = @ruc
              AND fb.mfb_cod_tipo_factura_boleta = @tipo
            ORDER BY fb.mfb_cod_factura_boleta DESC;";
        cmd.Parameters.AddWithValue("@folio", folio.Trim());
        cmd.Parameters.AddWithValue("@ruc", ruc.Trim());
        cmd.Parameters.AddWithValue("@tipo", codTipoDoc);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    /// <summary>
    /// Lee del comprobante de compra lo que el informe tiene que cancelar:
    ///
    ///   - la linea del proveedor: la que apunta a la factura (docorigen) y va
    ///     al abono. Da la cuenta (4212030, 4212040, 4240010, 4240020...), el
    ///     analisis, el tipo y el numero de documento, y el total.
    ///   - la linea 1419010 al cargo, si el gasto es parcial: lo que paga el trabajador.
    ///
    /// Se copian tal cual de compras en vez de recalcularlos para que el informe
    /// cancele exactamente lo que se registro, aunque se haya registrado a mano
    /// (en 2606175 Contabilidad uso para el RxH el analisis 4946, que es el que
    /// tenia la linea del proveedor en 2606177).
    /// Devuelve null si la factura no existe o su comprobante no tiene esa linea.
    /// </summary>
    public async Task<Modelo.DocumentoCompra?> LeerDocumentoCompraAsync(int codFactura, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT fb.mfb_cod_referencia, fb.mfb_cod_moneda,
                   p.mdco_cod_plan_cuenta, p.mpc_codigo_cuenta, p.mdco_cod_analisis,
                   p.mdco_cod_tipo_documento_contable, p.mdco_numero_documento, p.mdco_abono,
                   t.mdco_cod_plan_cuenta, t.mdco_cargo
            FROM dbo.mae_factura_boleta fb
            OUTER APPLY (
                SELECT TOP 1 d.mdco_cod_plan_cuenta, pc.mpc_codigo_cuenta, d.mdco_cod_analisis,
                             d.mdco_cod_tipo_documento_contable, d.mdco_numero_documento, d.mdco_abono
                FROM dbo.mae_detalle_comprobante_contable d
                INNER JOIN dbo.mae_plan_cuenta pc ON pc.mpc_cod_plan_cuenta = d.mdco_cod_plan_cuenta
                WHERE d.mdco_cod_comprobante_contable = fb.mfb_cod_referencia
                  AND d.mdco_cod_documento_origen = fb.mfb_cod_factura_boleta
                  AND d.mdco_abono > 0
                ORDER BY d.mdco_abono DESC) p
            OUTER APPLY (
                SELECT TOP 1 d.mdco_cod_plan_cuenta, d.mdco_cargo
                FROM dbo.mae_detalle_comprobante_contable d
                INNER JOIN dbo.mae_plan_cuenta pc ON pc.mpc_cod_plan_cuenta = d.mdco_cod_plan_cuenta
                WHERE d.mdco_cod_comprobante_contable = fb.mfb_cod_referencia
                  AND LTRIM(RTRIM(pc.mpc_codigo_cuenta)) = @cuentaPersonal
                  AND d.mdco_cargo > 0) t
            WHERE fb.mfb_cod_factura_boleta = @factura;";
        cmd.Parameters.AddWithValue("@factura", codFactura);
        cmd.Parameters.AddWithValue("@cuentaPersonal", Modelo.ConstantesErp.CuentaPorCobrarPersonal);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        if (rd.IsDBNull(0) || rd.IsDBNull(2)) return null;

        return new Modelo.DocumentoCompra(
            CodFacturaBoleta: codFactura,
            CodComprobanteCompra: Convert.ToInt32(rd.GetValue(0)),
            CodMoneda: Convert.ToByte(rd.GetValue(1)),
            CodPlanCuentaProveedor: Convert.ToInt32(rd.GetValue(2)),
            CuentaProveedor: rd.GetString(3).Trim(),
            CodAnalisisProveedor: Convert.ToInt64(rd.GetValue(4)),
            CodTipoDocumento: Convert.ToInt16(rd.GetValue(5)),
            NumeroDocumento: rd.IsDBNull(6) ? "" : rd.GetString(6).Trim(),
            MontoDocumento: Convert.ToDecimal(rd.GetValue(7)),
            CodPlanCuentaPersonal: rd.IsDBNull(8) ? null : Convert.ToInt32(rd.GetValue(8)),
            MontoPersonal: rd.IsDBNull(9) ? 0m : Convert.ToDecimal(rd.GetValue(9)));
    }

    /// <summary>
    /// Busca si el documento ya se cancelo en otro comprobante: una linea al CARGO
    /// en la misma cuenta y analisis del proveedor, con el mismo numero de
    /// documento y por el total, fuera del comprobante de compra. Pasa cuando
    /// Contabilidad ya lo rindio a mano (E001-3527 en 2606128) o ya se le pago al
    /// proveedor. Cancelarlo otra vez dejaria el proveedor con saldo deudor.
    /// No se compara el tipo de documento porque a mano a veces cambia
    /// (3347246 es tipo 37 en compras y 25 en el informe 2606152).
    /// </summary>
    public async Task<int?> BuscarCancelacionExistenteAsync(Modelo.DocumentoCompra doc, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 d.mdco_cod_comprobante_contable
            FROM dbo.mae_detalle_comprobante_contable d
            WHERE d.mdco_cod_plan_cuenta = @cuenta
              AND d.mdco_cod_analisis = @analisis
              AND LTRIM(RTRIM(d.mdco_numero_documento)) = @numero
              AND d.mdco_cargo = @monto
              AND d.mdco_cod_comprobante_contable <> @compra
            ORDER BY d.mdco_cod_comprobante_contable DESC;";
        cmd.Parameters.AddWithValue("@cuenta", doc.CodPlanCuentaProveedor);
        cmd.Parameters.AddWithValue("@analisis", doc.CodAnalisisProveedor);
        cmd.Parameters.AddWithValue("@numero", doc.NumeroDocumento.Trim());
        cmd.Parameters.AddWithValue("@monto", doc.MontoDocumento);
        cmd.Parameters.AddWithValue("@compra", doc.CodComprobanteCompra);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    /// <summary>
    /// Busca una transferencia (entrega de fondo o solicitud aprobada) que ya
    /// este registrada. Coincide cuenta, analisis de la persona y monto al cargo,
    /// y ademas una de estas dos cosas:
    ///
    ///   - el mismo numero de documento (ddMMyyyy del movimiento), o
    ///   - una glosa que termina con el mismo titulo, en un comprobante del mismo
    ///     tipo. Hace falta porque a mano se graba con la fecha del dia en que se
    ///     registra: el 2606190 de "VIATICOS PIURA" quedo con numero 16092026 y
    ///     la solicitud se aprobo el 17.
    ///
    /// La glosa no se compara entera porque empieza con el folio ("TR-7"), que
    /// solo se conoce al grabar.
    /// </summary>
    public async Task<int?> BuscarTransferenciaFondoExistenteAsync(
        int codPlanCuentaFondo, long codAnalisis, string numeroDocumento, decimal monto,
        string titulo, short tipoComprobante, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 d.mdco_cod_comprobante_contable
            FROM dbo.mae_detalle_comprobante_contable d
            INNER JOIN dbo.mae_comprobante_contable c
                    ON c.mcm_cod_comprobante_contable = d.mdco_cod_comprobante_contable
            WHERE d.mdco_cod_plan_cuenta = @cuenta
              AND d.mdco_cod_analisis = @analisis
              AND d.mdco_cargo = @monto
              AND (LTRIM(RTRIM(d.mdco_numero_documento)) = @numero
                   OR (c.mcm_cod_tipo_comprobante = @tipo
                       AND LTRIM(RTRIM(c.mcm_glosa)) LIKE @titulo ESCAPE '\'))
            ORDER BY d.mdco_cod_comprobante_contable DESC;";
        cmd.Parameters.AddWithValue("@cuenta", codPlanCuentaFondo);
        cmd.Parameters.AddWithValue("@analisis", codAnalisis);
        cmd.Parameters.AddWithValue("@numero", numeroDocumento.Trim());
        cmd.Parameters.AddWithValue("@monto", monto);
        cmd.Parameters.AddWithValue("@tipo", tipoComprobante);
        cmd.Parameters.AddWithValue("@titulo", "%" + EscaparLike(titulo.Trim()));

        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    /// <summary>Escapa los comodines de LIKE para comparar un texto tal cual.</summary>
    private static string EscaparLike(string texto) => texto
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_")
        .Replace("[", "\\[");

    /// <summary>
    /// Busca un comprobante de rendicion que ya exista con la misma glosa, tipo,
    /// cuenta de contrapartida y monto. Un informe no tiene una llave natural como
    /// el folio de una factura; esta es la forma de no duplicar uno que
    /// Contabilidad ya registro a mano (por ejemplo 2606175).
    /// </summary>
    public async Task<int?> BuscarComprobanteRendicionExistenteAsync(
        short tipoComprobante, string glosa, int codPlanCuentaContrapartida, decimal abono, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 c.mcm_cod_comprobante_contable
            FROM dbo.mae_comprobante_contable c
            INNER JOIN dbo.mae_detalle_comprobante_contable d
                    ON d.mdco_cod_comprobante_contable = c.mcm_cod_comprobante_contable
            WHERE c.mcm_cod_tipo_comprobante = @tipo
              AND LTRIM(RTRIM(c.mcm_glosa)) = @glosa
              AND d.mdco_cod_plan_cuenta = @cuenta
              AND d.mdco_abono = @abono
            ORDER BY c.mcm_cod_comprobante_contable DESC;";
        cmd.Parameters.AddWithValue("@tipo", tipoComprobante);
        cmd.Parameters.AddWithValue("@glosa", glosa.Trim());
        cmd.Parameters.AddWithValue("@cuenta", codPlanCuentaContrapartida);
        cmd.Parameters.AddWithValue("@abono", abono);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    /// <summary>
    /// Busca el analisis contable de un empleado por su documento de identidad.
    ///
    /// La contrapartida de una rendicion va a nombre de quien rinde, y esas
    /// cuentas exigen analisis (mpc_requiere_analisis = 1). El documento llega
    /// en el campo extra "Ruc Proveedor" del gasto.
    ///
    /// Se compara sin ceros a la izquierda porque tran_analisis guarda el
    /// documento como numero: Rindegastos manda "09601820" y el ERP tiene
    /// 9601820.
    ///
    /// Se busca igual que Ingreso de Comprobante (clsADAnalisis.mtdSelectAnalisis):
    /// solo por codigo interno, sin filtrar por tabla asociada, y se toma la
    /// primera fila. No se filtra por 'EM' porque muchos empleados solo tienen
    /// analisis de cliente ('CL') y es el que Contabilidad usa en sus
    /// rendiciones (p. ej. 124372 de Yulianna Malma en 2598106 y 2600293).
    /// El ORDER BY es el del indice clustered, que es lo que devuelve la
    /// pantalla sin ORDER BY.
    /// </summary>
    public async Task<(long Cod, string Nombre)?> BuscarAnalisisEmpleadoAsync(
        string documento, CancellationToken ct)
    {
        var limpio = LimpiarDocumento(documento);
        if (limpio is null) return null;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 tan_cod_analisis, tan_nombre
            FROM dbo.tran_analisis
            WHERE tan_cod_interno_analisis = @doc
            ORDER BY tan_cod_analisis;";
        cmd.Parameters.AddWithValue("@doc", limpio.Value);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.GetInt64(0), rd.GetString(1).Trim());
    }

    /// <summary>
    /// Analisis que el ERP enlaza con una cuenta contable. En Ingreso de
    /// Comprobante, al elegir la cuenta del banco el analisis se llena solo:
    /// la pantalla llama a este mismo SP (clsADPlanCuenta.mtdSelectRucEnlazadoconCuentaContable,
    /// usp_enlazaCuentaconAnalisis). Para 1041060 devuelve el analisis 765,
    /// Banco de Credito del Peru. Devuelve null si la cuenta no tiene enlace.
    /// </summary>
    public async Task<long?> BuscarAnalisisDeCuentaAsync(string codigoCuenta, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "dbo.usp_enlazaCuentaconAnalisis";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@Codcuenta", codigoCuenta.Trim());

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        var col = rd.GetOrdinal("ttan_cod_analisis");
        return rd.IsDBNull(col) ? null : Convert.ToInt64(rd.GetValue(col));
    }

    /// <summary>
    /// Nombre corto del empleado (men_nombre_corto). Es el que Contabilidad pone
    /// en la glosa de la transferencia de un fondo.
    /// </summary>
    public async Task<string?> BuscarNombreCortoEmpleadoAsync(string documento, CancellationToken ct)
    {
        var limpio = LimpiarDocumento(documento);
        if (limpio is null) return null;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 LTRIM(RTRIM(ISNULL(men_nombre_corto,'')))
            FROM dbo.mae_empleado
            WHERE TRY_CAST(LTRIM(RTRIM(men_rut)) AS BIGINT) = @doc;";
        cmd.Parameters.AddWithValue("@doc", limpio.Value);

        var r = await cmd.ExecuteScalarAsync(ct);
        var nombre = r is null or DBNull ? null : Convert.ToString(r);
        return string.IsNullOrWhiteSpace(nombre) ? null : nombre;
    }

    /// <summary>
    /// Busca al empleado por documento, aunque no tenga analisis contable.
    /// Sirve para dar un mensaje util: no es lo mismo "esta persona no existe"
    /// que "existe pero le falta el analisis contable".
    /// </summary>
    public async Task<string?> BuscarNombreEmpleadoAsync(string documento, CancellationToken ct)
    {
        var limpio = LimpiarDocumento(documento);
        if (limpio is null) return null;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 LTRIM(RTRIM(ISNULL(men_nombres,'') + ' ' +
                                     ISNULL(men_apellido_paterno,'') + ' ' +
                                     ISNULL(men_apellido_materno,'')))
            FROM dbo.mae_empleado
            WHERE TRY_CAST(LTRIM(RTRIM(men_rut)) AS BIGINT) = @doc;";
        cmd.Parameters.AddWithValue("@doc", limpio.Value);

        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToString(r);
    }

    /* Los dos correlativos de abajo se calculan con MAX + 1 y no contando filas,
       por dos motivos que se ven en los datos reales:

         - Cada liquidacion de caja chica deja DOS lineas con el mismo numero
           (la liquidacion y su contrapartida), asi que contar daria el doble.
         - Hay numeros repetidos y huecos por errores de digitacion (falta el
           0008 de una persona, y hay un "2026-023" de tres digitos).

       Tomar el maximo ya usado es estable frente a las dos cosas. */

    /// <summary>
    /// Siguiente correlativo de reembolso para una persona y una fecha de pago.
    ///
    /// El numero es ddMMyyyy del viernes mas dos digitos, y esos dos digitos
    /// cuentan los reembolsos de esa misma persona para ese mismo viernes:
    /// Carlos Cisneros tiene 1206202601, 1206202602 y 1206202603, mientras que
    /// otras personas del mismo viernes arrancan de nuevo en 01.
    /// </summary>
    /// <param name="cuenta">
    /// 4699210 en soles o 4699220 en dolares. Cada cuenta lleva su propia
    /// numeracion, igual que en el historico.
    /// </param>
    public async Task<int> SiguienteCorrelativoReembolsoAsync(
        string cuenta, long codAnalisis, DateTime fechaVencimiento, CancellationToken ct)
    {
        var prefijo = fechaVencimiento.ToString("ddMMyyyy");

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(LTRIM(RTRIM(d.mdco_numero_documento)),
                                                 LEN(@prefijo) + 1, 10) AS INT)), 0)
            FROM dbo.mae_detalle_comprobante_contable d
            INNER JOIN dbo.mae_plan_cuenta p
                    ON p.mpc_cod_plan_cuenta = d.mdco_cod_plan_cuenta
            WHERE LTRIM(RTRIM(p.mpc_codigo_cuenta)) = @cuenta
              AND d.mdco_cod_analisis = @analisis
              AND LTRIM(RTRIM(d.mdco_numero_documento)) LIKE @prefijo + '%';";
        cmd.Parameters.AddWithValue("@cuenta", cuenta);
        cmd.Parameters.AddWithValue("@analisis", codAnalisis);
        cmd.Parameters.AddWithValue("@prefijo", prefijo);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) + 1;
    }

    /// <summary>
    /// Siguiente correlativo de liquidacion de caja chica.
    ///
    /// El numero es "2026-0001" y va POR PERSONA, no por empresa: en 2026 el
    /// numero 2026-0001 aparece 34 veces, una por cada persona que hizo su
    /// primera liquidacion del anio. Cada quien lleva su propia secuencia
    /// (0001, 0002, 0003...) y todas reinician el 1 de enero.
    /// </summary>
    /// <param name="cuenta">4690210 en soles o 4690215 en dolares.</param>
    public async Task<int> SiguienteCorrelativoCajaChicaAsync(
        string cuenta, long codAnalisis, int anio, CancellationToken ct)
    {
        var prefijo = anio.ToString() + "-";

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(LTRIM(RTRIM(d.mdco_numero_documento)),
                                                 LEN(@prefijo) + 1, 10) AS INT)), 0)
            FROM dbo.mae_detalle_comprobante_contable d
            INNER JOIN dbo.mae_plan_cuenta p
                    ON p.mpc_cod_plan_cuenta = d.mdco_cod_plan_cuenta
            WHERE LTRIM(RTRIM(p.mpc_codigo_cuenta)) = @cuenta
              AND d.mdco_cod_analisis = @analisis
              AND LTRIM(RTRIM(d.mdco_numero_documento)) LIKE @prefijo + '%';";
        cmd.Parameters.AddWithValue("@cuenta", cuenta);
        cmd.Parameters.AddWithValue("@analisis", codAnalisis);
        cmd.Parameters.AddWithValue("@prefijo", prefijo);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) + 1;
    }

    /// <summary>
    /// Deja el documento como numero para poder compararlo con tran_analisis,
    /// que lo guarda sin ceros a la izquierda ni separadores.
    /// </summary>
    private static long? LimpiarDocumento(string documento)
    {
        var soloDigitos = new string(documento.Where(char.IsDigit).ToArray());
        return long.TryParse(soloDigitos, out var n) ? n : null;
    }
}
