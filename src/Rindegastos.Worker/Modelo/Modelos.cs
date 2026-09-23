namespace Rindegastos.Worker.Modelo;

/// <summary>Estados del campo rg_gasto.rgg_estado.</summary>
public static class EstadoGasto
{
    /// <summary>El JSON esta en staging. Contabilidad no fue tocada.</summary>
    public const byte Descargado = 0;

    /// <summary>Todos los codigos del ERP resolvieron correctamente.</summary>
    public const byte Homologado = 1;

    /// <summary>El comprobante existe en el ERP (commit local hecho).</summary>
    public const byte Contabilizado = 2;

    /// <summary>Rindegastos acepto la marca de integracion. Fin del flujo.</summary>
    public const byte Confirmado = 3;

    /// <summary>Requiere intervencion manual.</summary>
    public const byte Error = 9;
}

/// <summary>
/// Constantes del ERP verificadas contra bd_epysa_peru.
/// Si alguna cambia, se cambia aqui y no en medio de la logica.
/// </summary>
public static class ConstantesErp
{
    /// <summary>ref_estado_global 43 = "Factura Compra Cerrada".</summary>
    public const short EstadoFacturaCompraCerrada = 43;

    /// <summary>ref_estado_global 19 = "Comprobante en Modificación". Es el que graba el ERP al crear.</summary>
    public const short EstadoComprobante = 19;

    /// <summary>
    /// ref_estado_global 17 = "Comprobante Abierto". Es el estado que el ERP registra
    /// en tran_auditoria_comprobante_contable, distinto del 19 que queda en el
    /// comprobante: la auditoria refleja el momento de crear el encabezado.
    /// </summary>
    public const short EstadoAuditoriaComprobanteAbierto = 17;

    /// <summary>ref_tipo_comprobante para facturas y boletas de compra.</summary>
    public const short TipoComprobanteCompra = 5;

    /// <summary>ref_tipo_comprobante para recibos por honorarios.</summary>
    public const short TipoComprobanteHonorarios = 10;

    /// <summary>rtdc_cod_tipo_documento_contable = 1 (FACTURA).</summary>
    public const short TipoDocFactura = 1;

    /// <summary>rtdc_cod_tipo_documento_contable = 14 (BOLETA DE VENTA).</summary>
    public const short TipoDocBoleta = 14;

    /// <summary>rtdc_cod_tipo_documento_contable = 15 (RECIBO DE HONORARIOS BH).</summary>
    public const short TipoDocReciboHonorarios = 15;

    /// <summary>
    /// rtdc_cod_tipo_documento_contable = 37 (COMPROBANTE NO DOMICILIADO).
    /// En wctrFacturaCompra2.ascx.vb la constante se llama IdCompDomiciliado,
    /// pero es el no domiciliado. Llega de Rindegastos con el codigo SUNAT 91.
    /// </summary>
    public const short TipoDocNoDomiciliado = 37;

    /// <summary>
    /// Tipos de documento para los que la pantalla genera sola la linea de IGV
    /// (4011020) al grabar, aunque el IGV sea cero. Es la lista de btnGrabar,
    /// lineas 1600-1608: factura, ticket, servicio publico, nota de credito,
    /// nota de debito, boleto de aviacion, DUA, DUA simplificada y no domiciliado.
    /// Boleta (14) y recibo por honorarios (15) no estan: por eso ahi no hay
    /// linea de IGV.
    /// </summary>
    public static readonly IReadOnlySet<short> TiposConLineaIgv =
        new HashSet<short> { 1, 52, 51, 6, 13, 34, 50, 41, 37 };

    /// <summary>Tipo de documento "sin documento" que el ERP usa en las lineas de gasto.</summary>
    public const short TipoDocSinDocumento = 27;

    /// <summary>Analisis generico. El ERP lo usa cuando la cuenta no requiere analisis.</summary>
    public const long AnalisisGenerico = 1;

    /// <summary>Cuenta de proveedores en moneda nacional.</summary>
    public const string CuentaProveedorMN = "4212030";

    /// <summary>Cuenta de proveedores en moneda extranjera.</summary>
    public const string CuentaProveedorME = "4212040";

    /// <summary>Cuenta de honorarios por pagar en moneda nacional.</summary>
    public const string CuentaHonorariosMN = "4240010";

    /// <summary>Cuenta de honorarios por pagar en moneda extranjera.</summary>
    public const string CuentaHonorariosME = "4240020";

    /// <summary>Cuenta de IGV credito fiscal.</summary>
    public const string CuentaIgv = "4011020";

    /// <summary>
    /// Otras cuentas por cobrar al personal. Es la contrapartida de un gasto
    /// PARCIAL: recoge la parte del documento que no asume la empresa y se le
    /// cobra al trabajador.
    ///
    /// No tiene version en moneda extranjera; en la BD se usa la misma cuenta
    /// tanto en soles como en dolares. Requiere analisis, tipo y numero de
    /// documento, pero NO centro de costo.
    /// </summary>
    public const string CuentaPorCobrarPersonal = "1419010";

    /// <summary>ref_moneda 1 = Soles.</summary>
    public const byte MonedaSoles = 1;

    /// <summary>Condicion de pago 1 = Efectivo.</summary>
    public const byte FormaPagoEfectivo = 1;
}

/// <summary>
/// Valores por defecto del bloque de no domiciliados de la pantalla, desde
/// "Periodo Dua" hasta "Modalidad del Serv. (A1. T20)". Rindegastos no manda
/// estos datos, asi que se graban siempre iguales, tal como los ingreso
/// Contabilidad en la factura no domiciliada 3347246 (registro 262038).
///
/// La pantalla solo habilita este bloque para el tipo 37 (lineas 575-610); en
/// los demas documentos queda bloqueado con sus valores vacios.
/// </summary>
public static class DatosNoDomiciliado
{
    /// <summary>Tipo Vinculacion Ec. (A1. T17): mae_tipo_vinculacion 1 = "00 Sin vinculacion".</summary>
    public const int TipoVinculacion = 1;

    /// <summary>Convenio Doble Trib. (A1. T18): mae_tipo_convenio_doble 1 = "00 NINGUNO".</summary>
    public const int ConvenioDobleTributacion = 1;

    /// <summary>Exoneraciones de Op. (A1. T21): mae_tipo_exoneraciones 1.</summary>
    public const int Exoneraciones = 1;

    /// <summary>Tipo Renta (A1. T19): mae_tipo_renta 4 = "03 Rentas de bienes situados o derechos utilizados en el pais".</summary>
    public const int TipoRenta = 4;

    /// <summary>Modalidad del Serv. (A1. T20): mae_tipo_modalidad_servicio 3 = "Servicio prestado exclusivamente en el extranjero".</summary>
    public const int ModalidadServicio = 3;

    /// <summary>Tipo Doc. Dua: el mismo tipo 37.</summary>
    public const short TipoDocDua = 37;

    /// <summary>N° Serie Doc. Dua y Doc. asoc. Dua.</summary>
    public const string SerieDua = "0";
    public const string FolioDua = "0";

    // Periodo Dua (FechaDocAsoc82): NO es un valor fijo. En el registro manual se
    // ingreso el 13/08/2026, pero el Libro de Compras No Domiciliados solo lee el
    // ANIO de ese campo (usp_ctb_neo_libro_compra_le_82v2, Campo12 =
    // LEFT(FechaDocAsoc82, 4)). Con la fecha fija, desde 2027 todo saldria como
    // 2026. Por eso se usa la fecha de contabilizacion: da el mismo anio que el
    // registro manual y sigue siendo correcta en los anios siguientes.
}

/// <summary>Un gasto ya homologado, listo para contabilizar.</summary>
public sealed class GastoHomologado
{
    public long IdRindegastos { get; init; }

    // Cabecera de mae_factura_boleta
    public string Folio { get; init; } = "";
    public int CodProveedor { get; init; }
    public string RucProveedor { get; init; } = "";
    public string RazonSocial { get; init; } = "";
    public long CodAnalisis { get; init; }
    public short CodTipoDocumento { get; init; }
    public byte CodMoneda { get; init; }
    public decimal TipoCambio { get; init; }
    public DateTime FechaEmision { get; init; }
    public DateTime FechaVencimiento { get; init; }
    public DateTime FechaContabilizacion { get; init; }
    public decimal MontoNeto { get; init; }
    public decimal MontoIgv { get; init; }
    public decimal MontoExento { get; init; }

    /// <summary>Total del documento. En un gasto parcial es el "Monto total" del campo extra.</summary>
    public decimal MontoTotal { get; init; }

    /// <summary>
    /// Parte del documento que asume la empresa. En un gasto normal es igual al
    /// total; en uno parcial es el OriginalAmount que manda Rindegastos.
    /// Sobre este monto se calculan el neto y el IGV.
    /// </summary>
    public decimal MontoEmpresa { get; init; }

    /// <summary>
    /// Parte que se le cobra al trabajador (total menos lo que asume la empresa).
    /// Cuando es mayor que cero, el asiento lleva una linea extra en 1419010.
    /// </summary>
    public decimal MontoPersonal => MontoTotal - MontoEmpresa;

    /// <summary>true cuando el documento se repartio entre la empresa y el trabajador.</summary>
    public bool EsParcial => MontoPersonal > 0m;

    // Linea de gasto
    public int CodPlanCuentaGasto { get; init; }
    public string CuentaGasto { get; init; } = "";
    public short? CodCentroCosto { get; init; }
    public string Glosa { get; init; } = "";

    /// <summary>true cuando el documento es Factura: se desagrega neto + IGV.</summary>
    public bool EsFactura => CodTipoDocumento == ConstantesErp.TipoDocFactura;

    /// <summary>true cuando es Recibo por Honorarios: usa la cuenta 4240010.</summary>
    public bool EsReciboHonorarios => CodTipoDocumento == ConstantesErp.TipoDocReciboHonorarios;

    /// <summary>true cuando es comprobante no domiciliado (tipo 37).</summary>
    public bool EsNoDomiciliado => CodTipoDocumento == ConstantesErp.TipoDocNoDomiciliado;

    /// <summary>true si la pantalla generaria sola la linea de IGV para este tipo de documento.</summary>
    public bool LlevaLineaIgv => ConstantesErp.TiposConLineaIgv.Contains(CodTipoDocumento);

    /// <summary>Glosa de la cabecera: "RAZON SOCIAL Nºfolio" (mismo formato que el ERP).</summary>
    public string GlosaCabecera => $"{RazonSocial} Nº{Folio}";
}

/// <summary>Resultado de intentar homologar un gasto.</summary>
public sealed class ResultadoHomologacion
{
    public GastoHomologado? Gasto { get; init; }
    public List<string> Errores { get; init; } = new();
    public bool Ok => Errores.Count == 0 && Gasto is not null;
}
