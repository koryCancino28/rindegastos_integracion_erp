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

    /// <summary>ref_moneda 1 = Soles.</summary>
    public const byte MonedaSoles = 1;

    /// <summary>Condicion de pago 1 = Efectivo.</summary>
    public const byte FormaPagoEfectivo = 1;
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
    public decimal MontoTotal { get; init; }

    // Linea de gasto
    public int CodPlanCuentaGasto { get; init; }
    public string CuentaGasto { get; init; } = "";
    public short? CodCentroCosto { get; init; }
    public string Glosa { get; init; } = "";

    /// <summary>true cuando el documento es Factura: se desagrega neto + IGV.</summary>
    public bool EsFactura => CodTipoDocumento == ConstantesErp.TipoDocFactura;

    /// <summary>true cuando es Recibo por Honorarios: usa la cuenta 4240010.</summary>
    public bool EsReciboHonorarios => CodTipoDocumento == ConstantesErp.TipoDocReciboHonorarios;

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
