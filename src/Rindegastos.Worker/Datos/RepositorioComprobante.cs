using System.Data;
using Microsoft.Data.SqlClient;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Datos;

/// <summary>Una linea del detalle del comprobante contable.</summary>
public sealed class LineaDetalle
{
    public int CodPlanCuenta { get; init; }
    public long CodAnalisis { get; init; } = ConstantesErp.AnalisisGenerico;
    public short? CodCentroCosto { get; init; }
    public short? CodItemGasto { get; init; }
    public short CodTipoDocumento { get; init; } = ConstantesErp.TipoDocSinDocumento;
    public int? CodDocumentoOrigen { get; init; }
    public string NumeroDocumento { get; init; } = "0";
    public DateTime? FechaVencimiento { get; init; }
    public decimal Cargo { get; init; }
    public decimal Abono { get; init; }
    public byte CodMoneda { get; init; }
    public decimal TipoCambio { get; init; }
    public string Glosa { get; init; } = "";
    public decimal CargoSoles { get; init; }
    public decimal AbonoSoles { get; init; }
}

/// <summary>Codigos generados en el ERP al contabilizar un gasto.</summary>
public sealed record ResultadoContabilizacion(int CodFacturaBoleta, int CodComprobante, int FolioComprobante);

/// <summary>
/// Graba la factura de compra y su comprobante contable en el ERP.
///
/// Reproduce paso a paso lo que hace la pantalla
/// Contabilidad > Comprobantes > Comprobantes Compra de Servicio sin Prorrateo
/// (wctrFacturaCompra2.ascx), usando los mismos stored procedures:
///
///   1. btnGrabar   -> GrabarFacturaCompra()          -> sp_insert_mae_factura_boleta_1v2
///                  -> GrabaCabeceraComprobanteFactura -> sp_insert_mae_comprobante_contable_1
///                  -> GrabaComprobanteEnFactura       -> mfb_cod_referencia
///   2. btnGrabaDetalle -> sp_insert_mae_detalle_comprobante_contable_1v2 (una vez por linea)
///
/// Todo ocurre dentro de una unica transaccion: o queda el documento completo,
/// o no queda nada.
/// </summary>
public sealed class RepositorioComprobante
{
    private readonly FabricaConexion _fabrica;
    private readonly ILogger<RepositorioComprobante> _logger;

    public RepositorioComprobante(FabricaConexion fabrica, ILogger<RepositorioComprobante> logger)
    {
        _fabrica = fabrica;
        _logger = logger;
    }

    public async Task<ResultadoContabilizacion> ContabilizarAsync(
        GastoHomologado g, IReadOnlyList<LineaDetalle> lineas, int codUsuario, CancellationToken ct)
    {
        // El comprobante debe cuadrar antes de tocar la base de datos.
        var totalCargo = lineas.Sum(l => l.CargoSoles);
        var totalAbono = lineas.Sum(l => l.AbonoSoles);
        if (Math.Abs(totalCargo - totalAbono) > 0.05m)
            throw new InvalidOperationException(
                $"Comprobante descuadrado: cargo {totalCargo:N2} vs abono {totalAbono:N2}.");

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            // ---- 1. Cabecera de la factura de compra -------------------------------
            await InsertarFacturaAsync(cn, tx, g, codUsuario, ct);

            var codFactura = await BuscarFacturaAsync(cn, tx, g, ct)
                ?? throw new InvalidOperationException("No se pudo recuperar el codigo de la factura recien creada.");

            // ---- 2. Documentos asociados (el ERP siempre crea esta fila) ------------
            await InsertarDocumentosAsociadosAsync(cn, tx, codFactura, g, ct);

            // ---- 3. Cabecera del comprobante contable ------------------------------
            var tipoComprobante = g.EsReciboHonorarios
                ? ConstantesErp.TipoComprobanteHonorarios
                : ConstantesErp.TipoComprobanteCompra;

            var anno = (short)g.FechaContabilizacion.Year;
            var mes = (byte)g.FechaContabilizacion.Month;
            var folioComprobante = await SiguienteFolioAsync(cn, tx, anno, mes, tipoComprobante, ct);

            await InsertarComprobanteAsync(cn, tx, g, anno, mes, tipoComprobante, folioComprobante, ct);

            var codComprobante = await BuscarComprobanteAsync(cn, tx, tipoComprobante, folioComprobante, anno, mes, ct)
                ?? throw new InvalidOperationException("No se pudo recuperar el codigo del comprobante recien creado.");

            // Auditoria de la cabecera, igual que hace clsComprobanteContable.
            await AuditarComprobanteAsync(cn, tx, g.GlosaCabecera, g.FechaContabilizacion,
                                          codComprobante, anno, mes,
                                          tipoComprobante, folioComprobante, codUsuario, ct);

            // ---- 4. Enlazar factura con comprobante (mfb_cod_referencia) -----------
            await EnlazarFacturaComprobanteAsync(cn, tx, codFactura, codComprobante, ct);

            // ---- 5. Lineas del detalle --------------------------------------------
            foreach (var linea in lineas)
            {
                // La linea del proveedor apunta a la factura como documento de origen.
                var origen = linea.CodDocumentoOrigen == -1 ? codFactura : linea.CodDocumentoOrigen;
                await InsertarDetalleAsync(cn, tx, codComprobante, linea, origen, ct);

                // Auditoria de la linea. El ERP la graba justo despues de cada insert,
                // buscando el ultimo detalle creado para ese comprobante.
                var codDetalle = await UltimoDetalleAsync(cn, tx, codComprobante, ct);
                if (codDetalle is not null)
                    await AuditarDetalleAsync(cn, tx, codComprobante, codDetalle.Value,
                                              linea, origen, codUsuario, DescripcionDetalle, ct);
            }

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Gasto {IdRg} contabilizado. Factura {Factura}, comprobante {Comprobante} (folio {Folio}/{Anno}-{Mes})",
                g.IdRindegastos, codFactura, codComprobante, folioComprobante, anno, mes);

            return new ResultadoContabilizacion(codFactura, codComprobante, folioComprobante);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Graba el comprobante de una rendicion (planilla de movilidad).
    ///
    /// Reproduce la pantalla Contabilidad > Comprobantes > Ingreso de Comprobante
    /// (wctrComprobanteContable.ascx). Se diferencia del flujo de compras en tres cosas:
    ///
    ///   - NO crea factura: la rendicion no tiene documento tributario detras,
    ///     asi que no se tocan mae_factura_boleta ni sus tablas relacionadas.
    ///   - El comprobante queda en estado 17 ("Comprobante Abierto") y no en 19.
    ///     En esa pantalla blnTermina se vuelve True en cada Page_Load, asi que
    ///     la rama del 19 no se alcanza (ver btnGrabar_Click, lineas 848-854).
    ///   - La auditoria del detalle dice "Ingresado en Contabilidad", porque la
    ///     pantalla pasa "Contabilidad" como descripcion (linea 998).
    ///
    /// Todo ocurre en una sola transaccion y se valida que cuadre antes de escribir.
    /// </summary>
    public async Task<ResultadoContabilizacion> ContabilizarInformeAsync(
        InformeHomologado inf, IReadOnlyList<LineaDetalle> lineas, int codUsuario, CancellationToken ct)
    {
        var totalCargo = lineas.Sum(l => l.CargoSoles);
        var totalAbono = lineas.Sum(l => l.AbonoSoles);
        if (Math.Abs(totalCargo - totalAbono) > 0.05m)
            throw new InvalidOperationException(
                $"Comprobante descuadrado: cargo {totalCargo:N2} vs abono {totalAbono:N2}.");

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            var anno = (short)inf.FechaContabilizacion.Year;
            var mes = (byte)inf.FechaContabilizacion.Month;
            var tipo = inf.Regla.TipoComprobante;

            // ---- 1. Cabecera del comprobante --------------------------------------
            var folio = await SiguienteFolioAsync(cn, tx, anno, mes, tipo, ct);

            await InsertarComprobanteCabeceraAsync(
                cn, tx, inf.FechaContabilizacion, anno, mes, tipo, folio,
                inf.GlosaCabecera, ConstantesErp.EstadoAuditoriaComprobanteAbierto, ct);

            var codComprobante = await BuscarComprobanteAsync(cn, tx, tipo, folio, anno, mes, ct)
                ?? throw new InvalidOperationException("No se pudo recuperar el codigo del comprobante recien creado.");

            await AuditarComprobanteAsync(cn, tx, inf.GlosaCabecera, inf.FechaContabilizacion,
                                          codComprobante, anno, mes, tipo, folio, codUsuario, ct);

            // ---- 2. Lineas: un gasto por linea, mas la contrapartida ---------------
            foreach (var linea in lineas)
            {
                await InsertarDetalleAsync(cn, tx, codComprobante, linea, linea.CodDocumentoOrigen, ct);

                var codDetalle = await UltimoDetalleAsync(cn, tx, codComprobante, ct);
                if (codDetalle is not null)
                    await AuditarDetalleAsync(cn, tx, codComprobante, codDetalle.Value,
                                              linea, linea.CodDocumentoOrigen, codUsuario,
                                              ConstantesRendicion.AuditoriaDetalle, ct);
            }

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Informe {IdRg} ({Tipo}) contabilizado. Comprobante {Comprobante} " +
                "(folio {Folio}/{Anno}-{Mes}), {N} lineas, total {Total:N2}",
                inf.IdRindegastos, inf.Regla.Nombre, codComprobante, folio, anno, mes,
                lineas.Count, inf.Total);

            // No hay factura, por eso el primer codigo va en 0.
            return new ResultadoContabilizacion(0, codComprobante, folio);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Graba la entrega de un fondo como comprobante de transferencia (tipo 19),
    /// en la misma pantalla de Ingreso de Comprobante:
    ///
    ///   1020152  analisis=persona  td=26  num=11092026  cargo  1000.00
    ///   1041060  analisis=banco    td=55  num=TR-3      abono  1000.00
    ///
    /// La glosa y el numero de la linea del banco llevan el folio ("TR-3"), que
    /// solo se conoce al grabar. Por eso el folio se calcula primero y con el se
    /// arman las dos lineas, dentro de la misma transaccion.
    /// </summary>
    public async Task<ResultadoContabilizacion> ContabilizarFondoAsync(
        FondoHomologado f, Servicios.ServicioAsientoFondo asiento, int codUsuario, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            var anno = (short)f.FechaDeposito.Year;
            var mes = (byte)f.FechaDeposito.Month;

            var folio = await SiguienteFolioAsync(cn, tx, anno, mes, ConstantesFondo.TipoComprobante, ct);
            var (glosa, lineas) = asiento.Construir(f, folio);
            var glosaCabecera = glosa.Length > 100 ? glosa[..100] : glosa;

            await InsertarComprobanteCabeceraAsync(
                cn, tx, f.FechaDeposito, anno, mes, ConstantesFondo.TipoComprobante, folio,
                glosaCabecera, ConstantesErp.EstadoAuditoriaComprobanteAbierto, ct);

            var codComprobante = await BuscarComprobanteAsync(
                    cn, tx, ConstantesFondo.TipoComprobante, folio, anno, mes, ct)
                ?? throw new InvalidOperationException("No se pudo recuperar el codigo del comprobante recien creado.");

            await AuditarComprobanteAsync(cn, tx, glosaCabecera, f.FechaDeposito, codComprobante,
                                          anno, mes, ConstantesFondo.TipoComprobante, folio, codUsuario, ct);

            foreach (var linea in lineas)
            {
                await InsertarDetalleAsync(cn, tx, codComprobante, linea, null, ct);

                var codDetalle = await UltimoDetalleAsync(cn, tx, codComprobante, ct);
                if (codDetalle is not null)
                    await AuditarDetalleAsync(cn, tx, codComprobante, codDetalle.Value, linea, null,
                                              codUsuario, ConstantesRendicion.AuditoriaDetalle, ct);
            }

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Fondo {IdFondo} deposito {N} contabilizado. Comprobante {Comprobante} (folio {Folio}/{Anno}-{Mes}), " +
                "{Monto:N2} a la cuenta {Cuenta}: {Glosa}",
                f.IdFondo, f.Deposito, codComprobante, folio, anno, mes, f.Monto, f.CuentaFondo, glosa);

            return new ResultadoContabilizacion(0, codComprobante, folio);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---------------------------------------------------------------- SPs del ERP

    /// <summary>sp_insert_mae_factura_boleta_1v2 (29 parametros, mismo orden que el ERP).</summary>
    private static async Task InsertarFacturaAsync(
        SqlConnection cn, SqlTransaction tx, GastoHomologado g, int codUsuario, CancellationToken ct)
    {
        await using var cmd = Sp(cn, tx, "sp_insert_mae_factura_boleta_1v2");
        var ahora = DateTime.Now;

        cmd.Parameters.AddWithValue("@mfb_fecha_emision_1", g.FechaEmision);
        cmd.Parameters.AddWithValue("@mfb_fecha_creacion_2", g.FechaContabilizacion);
        cmd.Parameters.AddWithValue("@mfb_fecha_vencimiento_3", g.FechaVencimiento);
        cmd.Parameters.AddWithValue("@mfb_folio_4", g.Folio);
        cmd.Parameters.AddWithValue("@mfb_cod_proveedor_5", g.CodProveedor);
        cmd.Parameters.AddWithValue("@mfb_cod_tipo_forma_pago_6", ConstantesErp.FormaPagoEfectivo);
        cmd.Parameters.AddWithValue("@mfb_cod_moneda_7", g.CodMoneda);
        cmd.Parameters.AddWithValue("@mfb_tipo_cambio_8", g.TipoCambio);
        cmd.Parameters.AddWithValue("@mfb_monto_neto_9", g.MontoNeto);
        cmd.Parameters.AddWithValue("@mfb_monto_exento_10", g.MontoExento);
        cmd.Parameters.AddWithValue("@mfb_monto_impuesto_11", g.MontoIgv);
        cmd.Parameters.AddWithValue("@mfb_monto_total_12", g.MontoTotal);
        cmd.Parameters.AddWithValue("@mfb_cod_estado_global_13", ConstantesErp.EstadoFacturaCompraCerrada);
        cmd.Parameters.AddWithValue("@mfb_cod_embarque_14", DBNull.Value);
        cmd.Parameters.AddWithValue("@mfb_pagada_15", DBNull.Value);
        cmd.Parameters.AddWithValue("@mfb_cod_referencia_16", DBNull.Value); // se actualiza despues
        cmd.Parameters.AddWithValue("@mfb_cod_tipo_factura_boleta_17", g.CodTipoDocumento);
        cmd.Parameters.AddWithValue("@mfb_monto_impuesto_especifico_18", 0m);
        cmd.Parameters.AddWithValue("@mfb_folio_detractacion_19", DBNull.Value);
        cmd.Parameters.AddWithValue("@mfb_fecha_detractacion_20", DBNull.Value);
        cmd.Parameters.AddWithValue("@mfb_cod_usuario_21", codUsuario);
        cmd.Parameters.AddWithValue("@mfb_fecha_proceso_22", ahora);
        cmd.Parameters.AddWithValue("@mfb_otros_impuesto_23", 0m);

        // Bloque de no domiciliados (A1. T17 a T21). La pantalla solo lo habilita
        // para el tipo 37; en los demas documentos queda en 0.
        var nd = g.EsNoDomiciliado;
        cmd.Parameters.AddWithValue("@mfb_cod_tipo_vinculacion_24", nd ? DatosNoDomiciliado.TipoVinculacion : 0);
        cmd.Parameters.AddWithValue("@mfb_cod_tipo_convenio_doble_25", nd ? DatosNoDomiciliado.ConvenioDobleTributacion : 0);
        cmd.Parameters.AddWithValue("@mfb_cod_tipo_exoneraciones_26", nd ? DatosNoDomiciliado.Exoneraciones : 0);
        cmd.Parameters.AddWithValue("@mfb_cod_tipo_renta_27", nd ? DatosNoDomiciliado.TipoRenta : 0);
        cmd.Parameters.AddWithValue("@mfb_cod_tipo_modalidad_servicio_28", nd ? DatosNoDomiciliado.ModalidadServicio : 0);
        cmd.Parameters.AddWithValue("@mfb_fecha_modificacion_29", ahora);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Recupera el codigo de la factura por su llave natural (folio + proveedor + tipo).
    /// Es mas seguro que ident_current, que puede devolver el id de otra sesion.
    /// </summary>
    private static async Task<int?> BuscarFacturaAsync(
        SqlConnection cn, SqlTransaction tx, GastoHomologado g, CancellationToken ct)
    {
        await using var cmd = Texto(cn, tx, @"
SELECT MAX(mfb_cod_factura_boleta)
FROM dbo.mae_factura_boleta
WHERE LTRIM(RTRIM(mfb_folio)) = @folio
  AND mfb_cod_proveedor = @prov
  AND mfb_cod_tipo_factura_boleta = @tipo;");
        cmd.Parameters.AddWithValue("@folio", g.Folio.Trim());
        cmd.Parameters.AddWithValue("@prov", g.CodProveedor);
        cmd.Parameters.AddWithValue("@tipo", g.CodTipoDocumento);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    /// <summary>
    /// sp_Graba_mae_factura_boleta_documentos_asociadosv2.
    /// El ERP siempre llama a este SP aunque no haya documento asociado.
    ///
    /// Los campos de documento asociado solo se llenan en notas de credito y debito
    /// (wctrFacturaCompra2.ascx.vb los pide para los tipos 6, 13, 69, 70, 58 y 40).
    /// Para factura, boleta y recibo por honorarios la pantalla los deja como estan
    /// y el ERP graba sus valores por defecto: la fecha del dia, tipo 0 y textos
    /// vacios. Verificado en la BD: 10199 filas de 2026 con ese patron, y las unicas
    /// 9 filas con NULL eran justamente las del worker antes de este cambio.
    /// Se replica el default del ERP para que ningun registro se vea distinto.
    ///
    /// Los campos "82" son el bloque Dua de la pantalla (Registro de Compras No
    /// Domiciliados). Solo se habilitan para el tipo 37; ahi se graban los
    /// valores de DatosNoDomiciliado, igual que en el registro manual 262038.
    /// </summary>
    private static async Task InsertarDocumentosAsociadosAsync(
        SqlConnection cn, SqlTransaction tx, int codFactura, GastoHomologado g,
        CancellationToken ct)
    {
        var fechaCreacion = g.FechaContabilizacion;
        var nd = g.EsNoDomiciliado;

        await using var cmd = Sp(cn, tx, "sp_Graba_mae_factura_boleta_documentos_asociadosv2");
        cmd.Parameters.AddWithValue("@mfb_cod_factura_boleta", codFactura);
        cmd.Parameters.AddWithValue("@imp_Cons_bolsa_plastico", 0m);
        cmd.Parameters.AddWithValue("@FechaDocAsoc", fechaCreacion);
        cmd.Parameters.AddWithValue("@NserieDocAsoc", "");
        cmd.Parameters.AddWithValue("@TipoDocAsoc", (short)0);
        cmd.Parameters.AddWithValue("@FolioDocAsoc", "");
        // Periodo Dua: el libro de no domiciliados solo usa el anio (ver DatosNoDomiciliado).
        cmd.Parameters.AddWithValue("@FechaDocAsoc82", fechaCreacion);
        cmd.Parameters.AddWithValue("@NserieDocAsoc82", nd ? DatosNoDomiciliado.SerieDua : "");
        cmd.Parameters.AddWithValue("@TipoDocAsoc82", nd ? DatosNoDomiciliado.TipoDocDua : (short)0);
        cmd.Parameters.AddWithValue("@FolioDocAsoc82", nd ? DatosNoDomiciliado.FolioDua : "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Siguiente folio del comprobante: MAX(mcm_folio) + 1 para el ano, mes y tipo.
    /// Es la misma logica de clsComprobanteContable.mtdBuscarUltimoFolio.
    /// </summary>
    private static async Task<int> SiguienteFolioAsync(
        SqlConnection cn, SqlTransaction tx, short anno, byte mes, short tipo, CancellationToken ct)
    {
        await using var cmd = Texto(cn, tx, @"
SELECT ISNULL(MAX(mcm_folio), 0)
FROM dbo.mae_comprobante_contable WITH (UPDLOCK, HOLDLOCK)
WHERE mcm_anno = @anno AND mcm_mes = @mes AND mcm_cod_tipo_comprobante = @tipo;");
        cmd.Parameters.AddWithValue("@anno", anno);
        cmd.Parameters.AddWithValue("@mes", mes);
        cmd.Parameters.AddWithValue("@tipo", tipo);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) + 1;
    }

    /// <summary>Cabecera del comprobante de una factura de compra (estado 19).</summary>
    private static Task InsertarComprobanteAsync(
        SqlConnection cn, SqlTransaction tx, GastoHomologado g,
        short anno, byte mes, short tipo, int folio, CancellationToken ct)
        => InsertarComprobanteCabeceraAsync(
            cn, tx, g.FechaContabilizacion, anno, mes, tipo, folio,
            g.GlosaCabecera, ConstantesErp.EstadoComprobante, ct);

    /// <summary>
    /// sp_insert_mae_comprobante_contable_1 (7 parametros).
    /// El estado se recibe porque cada pantalla graba uno distinto: compras deja
    /// 19 ("Comprobante en Modificacion") e ingreso de comprobante deja 17
    /// ("Comprobante Abierto").
    /// </summary>
    private static async Task InsertarComprobanteCabeceraAsync(
        SqlConnection cn, SqlTransaction tx, DateTime fechaProceso,
        short anno, byte mes, short tipo, int folio, string glosaCabecera,
        short estado, CancellationToken ct)
    {
        var glosa = glosaCabecera;
        if (glosa.Length > 100) glosa = glosa[..100];   // mcm_glosa es varchar(100)

        await using var cmd = Sp(cn, tx, "sp_insert_mae_comprobante_contable_1");
        cmd.Parameters.AddWithValue("@mcm_fecha_proceso_1", fechaProceso);
        cmd.Parameters.AddWithValue("@mcm_anno_2", anno);
        cmd.Parameters.AddWithValue("@mcm_mes_3", mes);
        cmd.Parameters.AddWithValue("@mcm_cod_tipo_comprobante_4", tipo);
        cmd.Parameters.AddWithValue("@mcm_folio_5", folio);
        cmd.Parameters.AddWithValue("@mcm_glosa_6", glosa);
        cmd.Parameters.AddWithValue("@mcm_cod_estado_global_actual_7", estado);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int?> BuscarComprobanteAsync(
        SqlConnection cn, SqlTransaction tx, short tipo, int folio, short anno, byte mes, CancellationToken ct)
    {
        await using var cmd = Texto(cn, tx, @"
SELECT MAX(mcm_cod_comprobante_contable)
FROM dbo.mae_comprobante_contable
WHERE mcm_cod_tipo_comprobante = @tipo AND mcm_folio = @folio
  AND mcm_anno = @anno AND mcm_mes = @mes;");
        cmd.Parameters.AddWithValue("@tipo", tipo);
        cmd.Parameters.AddWithValue("@folio", folio);
        cmd.Parameters.AddWithValue("@anno", anno);
        cmd.Parameters.AddWithValue("@mes", mes);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    /// <summary>Equivale a GrabaComprobanteEnFactura(): deja el comprobante en mfb_cod_referencia.</summary>
    private static async Task EnlazarFacturaComprobanteAsync(
        SqlConnection cn, SqlTransaction tx, int codFactura, int codComprobante, CancellationToken ct)
    {
        await using var cmd = Texto(cn, tx,
            "UPDATE dbo.mae_factura_boleta SET mfb_cod_referencia = @comp WHERE mfb_cod_factura_boleta = @fact;");
        cmd.Parameters.AddWithValue("@comp", codComprobante);
        cmd.Parameters.AddWithValue("@fact", codFactura);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>sp_insert_mae_detalle_comprobante_contable_1v2 (17 parametros).</summary>
    private static async Task InsertarDetalleAsync(
        SqlConnection cn, SqlTransaction tx, int codComprobante,
        LineaDetalle l, int? origen, CancellationToken ct)
    {
        var glosa = l.Glosa.Length > 150 ? l.Glosa[..150] : l.Glosa;   // mdco_glosa es varchar(150)
        var numDoc = l.NumeroDocumento.Length > 20 ? l.NumeroDocumento[..20] : l.NumeroDocumento;

        await using var cmd = Sp(cn, tx, "sp_insert_mae_detalle_comprobante_contable_1v2");
        cmd.Parameters.AddWithValue("@mdco_cod_comprobante_contable_1", codComprobante);
        cmd.Parameters.AddWithValue("@mdco_cod_plan_cuenta_2", l.CodPlanCuenta);
        cmd.Parameters.AddWithValue("@mdco_cod_analisis_3", l.CodAnalisis);
        cmd.Parameters.AddWithValue("@mdco_cod_centro_costo_4", (object?)l.CodCentroCosto ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mdco_cod_item_gasto_5", (object?)l.CodItemGasto ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mdco_cod_tipo_documento_contable_6", l.CodTipoDocumento);
        cmd.Parameters.AddWithValue("@mdco_cod_documento_origen_7", (object?)origen ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mdco_numero_documento_8", numDoc);
        cmd.Parameters.AddWithValue("@mdco_fecha_vencimiento_9", (object?)l.FechaVencimiento ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mdco_cargo_10", l.Cargo);
        cmd.Parameters.AddWithValue("@mdco_abono_11", l.Abono);
        cmd.Parameters.AddWithValue("@mdco_cod_moneda_12", l.CodMoneda);
        cmd.Parameters.AddWithValue("@mdco_monto_tipo_cambio_13", l.TipoCambio);
        cmd.Parameters.AddWithValue("@mdco_glosa_14", glosa);
        cmd.Parameters.AddWithValue("@mdco_conciliacion_15", false);
        cmd.Parameters.AddWithValue("@mdco_cargo_soles_16", l.CargoSoles);
        cmd.Parameters.AddWithValue("@mdco_abono_soles_17", l.AbonoSoles);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- auditoria

    /* El ERP deja rastro de todo lo que graba en dos tablas de auditoria. Sin esto,
       los documentos de la integracion se verian iguales que los manuales, pero no
       aparecerian en los reportes de auditoria contable.

         tran_auditoria_comprobante_contable          1 fila por comprobante
         tran_auditoria_detalle_comprobante_trigger   1 fila por linea del detalle

       Los textos de descripcion son los mismos que usa la pantalla, para que un
       comprobante automatico y uno manual se lean igual en el reporte. */

    /* Textos verificados con DATALENGTH contra las filas reales del ERP:
       "Ingreso Encabezado Por Contabilidad" -> 35 bytes (18946 filas en 2026)
       "Ingresado en Detalle Por Compras."   -> 33 bytes (36493 filas desde ago/2026)
       El codigo VB del ERP escribe la primera con un espacio al final, pero la capa
       de datos lo recorta antes de grabar; aqui se guarda ya recortado. */
    private const string DescripcionCabecera = "Ingreso Encabezado Por Contabilidad";
    private const string DescripcionDetalle = "Ingresado en Detalle Por Compras.";

    /// <summary>sp_insert_tran_Auditoria_Comprobante_Contable_1 (14 parametros).</summary>
    private static async Task AuditarComprobanteAsync(
        SqlConnection cn, SqlTransaction tx, string glosaCabecera, DateTime fechaProceso,
        int codComprobante, short anno, byte mes, short tipo, int folio,
        int codUsuario, CancellationToken ct)
    {
        var glosa = glosaCabecera;
        if (glosa.Length > 100) glosa = glosa[..100];

        await using var cmd = Sp(cn, tx, "sp_insert_tran_Auditoria_Comprobante_Contable_1");
        cmd.Parameters.AddWithValue("@tac_nro_cod_comprobante_1", (long)codComprobante);
        cmd.Parameters.AddWithValue("@tac_fecha_proceso_2", fechaProceso);
        cmd.Parameters.AddWithValue("@tac_anno_3", anno);
        cmd.Parameters.AddWithValue("@tac_mes_4", mes);
        cmd.Parameters.AddWithValue("@tac_cod_tipo_Comprobante_5", tipo);
        cmd.Parameters.AddWithValue("@tac_folio_comprobante_6", folio.ToString());
        cmd.Parameters.AddWithValue("@tac_glosa_7", glosa);
        // La auditoria graba 17 ("Comprobante Abierto"), no el 19 del comprobante:
        // es el estado en el momento de crear el encabezado, antes del detalle.
        // Ver clsComprobanteContable.vb linea 995 (objComprobante.EstadoActual = 17).
        cmd.Parameters.AddWithValue("@tac_cod_estado_global_actual_8", (int)ConstantesErp.EstadoAuditoriaComprobanteAbierto);
        cmd.Parameters.AddWithValue("@tac_descripcion_9", DescripcionCabecera);
        cmd.Parameters.AddWithValue("@tac_procedimiento_10", DBNull.Value);
        cmd.Parameters.AddWithValue("@tac_cod_usuario_11", codUsuario);
        cmd.Parameters.AddWithValue("@tac_hora_12", DateTime.Now);
        cmd.Parameters.AddWithValue("@tac_string_sql_13", DBNull.Value);
        cmd.Parameters.AddWithValue("@tac_source_14", DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Codigo del ultimo detalle grabado para ese comprobante.</summary>
    private static async Task<long?> UltimoDetalleAsync(
        SqlConnection cn, SqlTransaction tx, int codComprobante, CancellationToken ct)
    {
        await using var cmd = Texto(cn, tx, @"
SELECT MAX(mdco_cod_detalle_comprobante_contable)
FROM dbo.mae_detalle_comprobante_contable
WHERE mdco_cod_comprobante_contable = @comp;");
        cmd.Parameters.AddWithValue("@comp", codComprobante);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToInt64(r);
    }

    /// <summary>
    /// sp_insert_tran_auditoria_detalle_comprobante_trigger_1 (22 parametros).
    /// El texto de <paramref name="obs"/> cambia segun la pantalla que graba:
    /// compras deja "Ingresado en Detalle Por Compras." y el ingreso de
    /// comprobante deja "Ingresado en Contabilidad".
    /// </summary>
    private static async Task AuditarDetalleAsync(
        SqlConnection cn, SqlTransaction tx, int codComprobante, long codDetalle,
        LineaDetalle l, int? origen, int codUsuario, string obs, CancellationToken ct)
    {
        // Ojo: aqui la glosa es varchar(100), no varchar(150) como en el detalle.
        var glosa = l.Glosa.Length > 100 ? l.Glosa[..100] : l.Glosa;
        var numDoc = l.NumeroDocumento.Length > 20 ? l.NumeroDocumento[..20] : l.NumeroDocumento;

        await using var cmd = Sp(cn, tx, "sp_insert_tran_auditoria_detalle_comprobante_trigger_1");
        cmd.Parameters.AddWithValue("@tacc_cod_detalle_comprobante_contable_1", codDetalle);
        cmd.Parameters.AddWithValue("@tacc_cod_comprobante_contable_2", (long)codComprobante);
        cmd.Parameters.AddWithValue("@tacc_cod_plan_cuenta_3", l.CodPlanCuenta);
        cmd.Parameters.AddWithValue("@tacc_cod_analisis_4", l.CodAnalisis);
        cmd.Parameters.AddWithValue("@tacc_centro_costo_5", (object?)l.CodCentroCosto ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tacc_cod_item_gasto_6", (object?)l.CodItemGasto ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tacc_cod_tipo_documento_contable_7", (int)l.CodTipoDocumento);
        cmd.Parameters.AddWithValue("@tacc_cod_documento_origen_8", (object?)origen ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tacc_numero_documento_9", numDoc);
        cmd.Parameters.AddWithValue("@tacc_fecha_vencimiento_10", (object?)l.FechaVencimiento ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tacc_cargo_11", l.Cargo);
        cmd.Parameters.AddWithValue("@tacc_abono_12", l.Abono);
        cmd.Parameters.AddWithValue("@tacc_cod_moneda_13", (int)l.CodMoneda);
        cmd.Parameters.AddWithValue("@tacc_monto_tipo_cambio_14", l.TipoCambio);
        cmd.Parameters.AddWithValue("@tacc_glosa_15", glosa);
        cmd.Parameters.AddWithValue("@tacc_conciliacion_16", false);
        cmd.Parameters.AddWithValue("@tacc_cargo_soles_17", l.CargoSoles);
        cmd.Parameters.AddWithValue("@tacc_abono_soles_18", l.AbonoSoles);
        cmd.Parameters.AddWithValue("@tacc_fecha_19", DateTime.Now);
        cmd.Parameters.AddWithValue("@tacc_usuario_20", codUsuario.ToString());
        cmd.Parameters.AddWithValue("@tacc_llamada_21", DBNull.Value);
        cmd.Parameters.AddWithValue("@tacc_obs_22", obs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- utilidades

    private static SqlCommand Sp(SqlConnection cn, SqlTransaction tx, string nombre)
    {
        var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = nombre;
        return cmd;
    }

    private static SqlCommand Texto(SqlConnection cn, SqlTransaction tx, string sql)
    {
        var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
