using System.Globalization;
using System.Text;

namespace Rindegastos.Worker.Modelo;

/// <summary>Cuentas y codigos propios del flujo de rendiciones.</summary>
public static class ConstantesRendicion
{
    /// <summary>
    /// Code del campo extra "Tipo de Documento" que identifica una planilla de
    /// movilidad. Es lo que separa los dos flujos:
    ///
    ///   PL          planilla de movilidad -> solo Ingreso de Comprobante (con el informe)
    ///   01, 03, R1  factura, boleta, RxH  -> Compras, y luego se cancela en el
    ///                                        comprobante del informe cuando se cierra
    ///
    /// La diferencia importa porque en una planilla de movilidad el campo
    /// "Ruc Proveedor" trae el DNI de la persona que rindio, mientras que en una
    /// boleta trae el RUC del comercio. Confundirlos cargaria la rendicion a
    /// nombre de Starbucks en vez de a nombre del empleado.
    /// </summary>
    public const string TipoDocumentoPlanillaMovilidad = "PL";

    // Cada rendicion tiene su cuenta en soles (MN) y en dolares (ME).
    // Las ME de reembolso y caja chica las indico Contabilidad; la de entrega a
    // rendir se tomo de la BD (ver CuentaEntregaARendirME).

    /// <summary>Entregas a Rendir Cta M.N. Se usa en entrega a rendir y en viaticos.</summary>
    public const string CuentaEntregaARendir = "1413110";

    /// <summary>
    /// Entregas a Rendir Cuenta ME. No la indico Contabilidad: se tomo de la BD,
    /// donde en 2026 aparece 27 veces al abono de comprobantes Diario en dolares
    /// con el mismo formato que la de soles (comprobante 2598232). PENDIENTE DE
    /// CONFIRMAR con Contabilidad.
    /// </summary>
    public const string CuentaEntregaARendirME = "1413010";

    /// <summary>Otras Cuentas por Pagar Diversas MN.</summary>
    public const string CuentaReembolso = "4699210";

    /// <summary>Otras Cuentas por Pagar Diversas ME: reembolso en dolares.</summary>
    public const string CuentaReembolsoME = "4699220";

    /// <summary>Ctas por Pagar Cajas Chicas MN.</summary>
    public const string CuentaCajaChica = "4690210";

    /// <summary>Ctas por Pagar Cajas Chicas ME: caja chica en dolares.</summary>
    public const string CuentaCajaChicaME = "4690215";

    /// <summary>
    /// Texto que el modulo de Ingreso de Comprobante deja en la auditoria del
    /// detalle. Distinto del de compras: aqui la pantalla pasa "Contabilidad"
    /// como descripcion y el ERP arma "Ingresado en " + descripcion.
    /// Ver wctrComprobanteContable.ascx.vb, linea 998.
    /// </summary>
    public const string AuditoriaDetalle = "Ingresado en Contabilidad";

    /// <summary>
    /// Status del INFORME cuando ya paso por todos los aprobadores y se cerro.
    /// Solo entonces se integra: un informe con Status 0 todavia puede cambiar.
    /// </summary>
    public const int InformeCerrado = 1;

    /// <summary>Status del GASTO aprobado.</summary>
    public const int GastoAprobado = 1;

    /// <summary>Status del GASTO rechazado: no entra en el comprobante del informe.</summary>
    public const int GastoRechazado = 2;
}

/// <summary>Los cuatro tipos de rendicion que maneja la planilla de movilidad.</summary>
public enum TipoRendicion
{
    EntregaARendir,
    Viaticos,
    Reembolso,
    CajaChica
}

/// <summary>
/// Todo lo que cambia entre un tipo de rendicion y otro, en un solo lugar.
///
/// Verificado contra los comprobantes que Contabilidad ingreso a mano:
///   2606122 Entrega a rendir  2606123 Viaticos
///   2606124 Reembolso         2606125 Caja chica
///
/// y contra el historico de 2026, donde cada cuenta aparece siempre con su
/// mismo tipo de comprobante (4690210 en Caja Egreso 842 veces, 1413110 en
/// Diario 148 veces, 4699210 en Diario 74 veces).
/// </summary>
public sealed record ReglaRendicion(
    TipoRendicion Tipo,
    string Nombre,
    short TipoComprobante,
    string CuentaContrapartida,
    string CuentaContrapartidaME)
{
    /// <summary>
    /// Cuenta de contrapartida segun la moneda: la MN para soles, la ME para
    /// cualquier moneda extranjera. Es la misma idea que 4212030/4212040 en compras.
    /// </summary>
    public string CuentaPara(byte codMoneda)
        => codMoneda == ConstantesErp.MonedaSoles ? CuentaContrapartida : CuentaContrapartidaME;

    /// <summary>ref_tipo_comprobante 17 = Diario.</summary>
    public const short ComprobanteDiario = 17;

    /// <summary>ref_tipo_comprobante 2 = Caja Egreso.</summary>
    public const short ComprobanteCajaEgreso = 2;

    /// <summary>rtdc_cod_tipo_documento_contable 26 = OTROS. Es el que lleva la contrapartida.</summary>
    public const short TipoDocOtros = 26;

    private static readonly ReglaRendicion[] Todas =
    {
        //                                                                   soles (MN)                               dolares (ME)
        new(TipoRendicion.EntregaARendir, "Entrega a rendir", ComprobanteDiario,
            ConstantesRendicion.CuentaEntregaARendir, ConstantesRendicion.CuentaEntregaARendirME),
        new(TipoRendicion.Viaticos,       "Viaticos",         ComprobanteDiario,
            ConstantesRendicion.CuentaEntregaARendir, ConstantesRendicion.CuentaEntregaARendirME),
        new(TipoRendicion.Reembolso,      "Reembolso",        ComprobanteDiario,
            ConstantesRendicion.CuentaReembolso,      ConstantesRendicion.CuentaReembolsoME),
        new(TipoRendicion.CajaChica,      "Caja Chica",       ComprobanteCajaEgreso,
            ConstantesRendicion.CuentaCajaChica,      ConstantesRendicion.CuentaCajaChicaME),
    };

    /// <summary>
    /// Traduce el texto del campo extra "Tipo de rendicion" del informe.
    ///
    /// Rindegastos manda ese campo con Code vacio, asi que la unica pista es el
    /// texto, y no llega limpio: "Viáticos " viene con tilde y con un espacio al
    /// final. Por eso se compara sin tildes, sin espacios y sin mayusculas.
    /// </summary>
    public static ReglaRendicion? Reconocer(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var buscado = Normalizar(texto);

        return buscado switch
        {
            "entregaarendir" => Todas[0],
            "viaticos"       => Todas[1],
            "reembolso"      => Todas[2],
            "cajachica"      => Todas[3],
            _                => null
        };
    }

    /// <summary>Nombres reconocidos, para poder mostrarlos en el mensaje de error.</summary>
    public static string NombresReconocidos => string.Join(", ", Todas.Select(r => r.Nombre));

    /// <summary>Quita tildes, espacios y mayusculas para poder comparar textos escritos a mano.</summary>
    private static string Normalizar(string texto)
    {
        var sinTildes = new StringBuilder();
        foreach (var c in texto.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c)) continue;
            sinTildes.Append(char.ToLowerInvariant(c));
        }
        return sinTildes.ToString();
    }
}

/// <summary>
/// Un documento (factura, boleta, RxH) ya registrado en el modulo de compras,
/// leido de su comprobante. El informe lo cancela con una linea al cargo en la
/// misma cuenta, analisis, tipo y numero de documento del proveedor.
/// </summary>
public sealed record DocumentoCompra(
    int CodFacturaBoleta,
    int CodComprobanteCompra,
    byte CodMoneda,
    int CodPlanCuentaProveedor,
    string CuentaProveedor,
    long CodAnalisisProveedor,
    short CodTipoDocumento,
    string NumeroDocumento,
    decimal MontoDocumento,        // abono del proveedor en compras: total del documento, en su moneda
    int? CodPlanCuentaPersonal,    // cuenta 1419010 del gasto parcial, si la tiene
    decimal MontoPersonal)         // cargo 1419010 en compras: lo que paga el trabajador (0 si no es parcial)
{
    /// <summary>Lo que asume la empresa: es lo que se le devuelve a quien rinde.</summary>
    public decimal MontoEmpresa => MontoDocumento - MontoPersonal;
}

/// <summary>Una linea de gasto de la rendicion, ya traducida a codigos del ERP.</summary>
public sealed class GastoRendicion
{
    public long IdRindegastos { get; init; }
    public int CodPlanCuenta { get; init; }
    public string CuentaGasto { get; init; } = "";
    public short? CodCentroCosto { get; init; }

    /// <summary>Lo que la empresa le devuelve a quien rinde. Suma a la contrapartida.</summary>
    public decimal Monto { get; init; }
    public string Glosa { get; init; } = "";

    /// <summary>
    /// null en una planilla de movilidad (la linea va a la cuenta de gasto).
    /// En una factura, boleta o RxH es el documento ya registrado en compras: la
    /// linea va a la cuenta del proveedor, no a la de gasto.
    /// </summary>
    public DocumentoCompra? Documento { get; init; }

    /// <summary>Banderas de la cuenta: deciden si se llenan analisis, tipo de documento, etc.</summary>
    public bool CuentaRequiereAnalisis { get; init; }
    public bool CuentaRequiereTipoDocumento { get; init; }
    public bool CuentaRequiereNumeroDocumento { get; init; }
    public bool CuentaRequiereFechaVencimiento { get; init; }
}

/// <summary>Un informe ya homologado, listo para convertirse en comprobante.</summary>
public sealed class InformeHomologado
{
    public long IdRindegastos { get; init; }
    public ReglaRendicion Regla { get; init; } = null!;

    /// <summary>Glosa de la cabecera y de la linea de contrapartida: el titulo del informe.</summary>
    public string Titulo { get; init; } = "";

    /// <summary>Persona que rinde. Es el analisis contable de la contrapartida.</summary>
    public long CodAnalisisEmpleado { get; init; }
    public string NombreEmpleado { get; init; } = "";
    public string DocumentoEmpleado { get; init; } = "";

    public DateTime FechaContabilizacion { get; init; }
    public byte CodMoneda { get; init; }
    public decimal TipoCambio { get; init; }

    /// <summary>Cuenta de la contrapartida, ya resuelta contra mae_plan_cuenta.</summary>
    public int CodPlanCuentaContrapartida { get; init; }

    /// <summary>Codigo de la cuenta de contrapartida elegida (MN o ME segun la moneda).</summary>
    public string CuentaContrapartida { get; init; } = "";

    /// <summary>Numero de documento de la contrapartida. Se calcula, no viene de la API.</summary>
    public string NumeroDocumentoContrapartida { get; init; } = "";

    /// <summary>Vencimiento de la contrapartida. En reembolso es el viernes calculado.</summary>
    public DateTime FechaVencimientoContrapartida { get; init; }

    /// <summary>Centro de costo de la contrapartida: el del informe.</summary>
    public short? CodCentroCostoContrapartida { get; init; }

    public List<GastoRendicion> Gastos { get; init; } = new();

    /// <summary>Suma de los gastos. Es el abono de la contrapartida.</summary>
    public decimal Total => Gastos.Sum(g => g.Monto);

    /// <summary>Glosa recortada a lo que aguanta mcm_glosa (varchar 100).</summary>
    public string GlosaCabecera => Titulo.Length > 100 ? Titulo[..100] : Titulo;
}

/// <summary>Resultado de intentar homologar un informe.</summary>
public sealed class ResultadoHomologacionInforme
{
    public InformeHomologado? Informe { get; init; }
    public List<string> Errores { get; init; } = new();
    public bool Ok => Errores.Count == 0 && Informe is not null;

    /// <summary>
    /// El informe todavia no se puede integrar pero no es un error: falta que se
    /// cierre en Rindegastos o que sus documentos terminen de entrar por compras.
    /// Se deja esperando sin gastar reintentos.
    /// </summary>
    public string? MotivoEspera { get; init; }

    /// <summary>
    /// Documento de quien rinde, aunque la homologacion falle. Se guarda en
    /// staging para que la consulta de monitoreo L3 muestre a quien le falta
    /// el analisis contable.
    /// </summary>
    public string? DocumentoEmpleado { get; init; }
}

/// <summary>Estados de rg_informe.rgi_estado. Mismos significados que rg_gasto.</summary>
public static class EstadoInforme
{
    public const byte Descargado = 0;
    public const byte Homologado = 1;
    public const byte Contabilizado = 2;
    public const byte Confirmado = 3;
    public const byte Error = 9;
}
