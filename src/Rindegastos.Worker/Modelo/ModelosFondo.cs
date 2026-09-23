namespace Rindegastos.Worker.Modelo;

/// <summary>
/// Codigos propios del tercer flujo: la ENTREGA DE FONDOS.
///
/// Cuando Contabilidad crea un fondo en Rindegastos (una caja chica, por
/// ejemplo) y le deposita dinero, en el ERP eso es una transferencia: sale del
/// banco de caja chica y entra a la cuenta del fondo, a nombre de la persona
/// que lo recibe.
///
/// Verificado contra el comprobante 2606159 que Contabilidad ingreso a mano el
/// 11/09/2026 por el fondo 926276 "CAJA CHICA CUSCO":
///
///   tipo 19, folio 3, glosa "TR-3 KORY CANCINO - CAJA CHICA CUSCO"
///     1020152  analisis=149671 (DNI 76775158)  td=26  num=11092026  cargo 1000.00
///     1041060  analisis=765 (BCP)              td=55  num=TR-3      abono 1000.00
/// </summary>
public static class ConstantesFondo
{
    /// <summary>ref_tipo_comprobante 19 = Banco Egreso-Transferencia (BET).</summary>
    public const short TipoComprobante = 19;

    /// <summary>
    /// Banco de Credito MN - 193-2181289-0-20 Caja Chica. Es la cuenta de donde
    /// sale el dinero (2606159 y 2606160). Su analisis no se elige: lo trae la
    /// cuenta enlazada (usp_enlazaCuentaconAnalisis).
    /// </summary>
    public const string CuentaBanco = "1041060";

    /// <summary>
    /// Banco de Credito ME - 193-2186354-1-92 Caja Chica, para fondos en dolares.
    /// PENDIENTE DE CONFIRMAR: no hay ningun fondo en dolares con que compararla.
    /// </summary>
    public const string CuentaBancoME = "1041055";

    /// <summary>rtdc 26 = OTROS (codigo SUNAT 99). Va en la linea de la cuenta del fondo.</summary>
    public const short TipoDocOtros = 26;

    /// <summary>rtdc 55 = TRANSFERENCIA DE FONDOS (codigo SUNAT 003). Va en la linea del banco.</summary>
    public const short TipoDocTransferencia = 55;

    /// <summary>Prefijo del numero de documento y de la glosa: "TR-" + folio.</summary>
    public const string Prefijo = "TR-";

    /// <summary>TransactionType 1 = deposito. Los tipos 3 y 4 son retiros (liquidaciones).</summary>
    public const int TransaccionDeposito = 1;

    /// <summary>Cuenta del banco segun la moneda del fondo.</summary>
    public static string CuentaBancoPara(byte codMoneda)
        => codMoneda == ConstantesErp.MonedaSoles ? CuentaBanco : CuentaBancoME;

    /// <summary>
    /// Zona horaria de Peru. La API manda las fechas en UTC
    /// ("2026-09-16T22:07:15.802Z"), y cinco horas mas tarde puede ser otro dia.
    /// </summary>
    public static DateTime FechaEnPeru(DateTime fecha)
    {
        var utc = fecha.Kind == DateTimeKind.Utc ? fecha : fecha.ToUniversalTime();
        return utc.AddHours(-5).Date;
    }

    /// <summary>"TR-3", tal como queda en el numero de documento del banco.</summary>
    public static string NumeroTransferencia(int folio) => Prefijo + folio;

    /// <summary>
    /// "TR-3 KORY CANCINO - CAJA CHICA CUSCO": el numero de transferencia, el
    /// nombre corto de quien recibe el fondo y el titulo del fondo.
    /// </summary>
    public static string Glosa(int folio, string nombreCorto, string titulo)
        => $"{NumeroTransferencia(folio)} {nombreCorto} - {titulo}".Trim();
}

/// <summary>
/// Una transferencia lista para grabar: el deposito de un fondo o la entrega de
/// una solicitud aprobada. Las dos tienen el mismo asiento; lo unico que cambia
/// es la cuenta de destino (la del fondo, o entregas a rendir en la solicitud).
/// </summary>
public sealed class FondoHomologado
{
    public long IdFondo { get; init; }

    /// <summary>Numero de deposito dentro del fondo: 1 el primero, 2 la recarga, etc.</summary>
    public short Deposito { get; init; }

    /// <summary>Id de la solicitud de fondo, cuando la transferencia viene de una.</summary>
    public string? IdSolicitud { get; init; }

    /// <summary>De donde salio, para el log: "fondo 926274 deposito 1" o "solicitud 6aab...".</summary>
    public string Referencia => IdSolicitud is null
        ? $"fondo {IdFondo} deposito {Deposito}"
        : $"solicitud {IdSolicitud}";

    /// <summary>Titulo del fondo o de la solicitud. Va en la glosa.</summary>
    public string Titulo { get; init; } = "";

    /// <summary>Documento de quien recibe el fondo (campo Code del fondo).</summary>
    public string DocumentoEmpleado { get; init; } = "";
    public long CodAnalisisEmpleado { get; init; }

    /// <summary>Nombre corto de mae_empleado. Va en la glosa.</summary>
    public string NombreCorto { get; init; } = "";

    /// <summary>
    /// Cuenta de destino: la del fondo (Description, por ejemplo 1020152) o la de
    /// entregas a rendir (1413110) cuando viene de una solicitud.
    /// </summary>
    public string CuentaFondo { get; init; } = "";
    public int CodPlanCuentaFondo { get; init; }

    /// <summary>Cuenta del banco de donde sale el dinero.</summary>
    public string CuentaBanco { get; init; } = "";
    public int CodPlanCuentaBanco { get; init; }
    public long CodAnalisisBanco { get; init; }

    public decimal Monto { get; init; }
    public byte CodMoneda { get; init; }
    public decimal TipoCambio { get; init; }

    /// <summary>
    /// Fecha del movimiento: la del deposito del fondo, o la de aprobacion de la
    /// solicitud. Es la fecha del comprobante y el vencimiento.
    /// </summary>
    public DateTime FechaDeposito { get; init; }

    /// <summary>Numero de documento de la linea del fondo: ddMMyyyy del deposito.</summary>
    public string NumeroDocumentoFondo => FechaDeposito.ToString("ddMMyyyy");

    public decimal MontoSoles => CodMoneda == ConstantesErp.MonedaSoles
        ? Monto
        : Math.Round(Monto * TipoCambio, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Resultado de intentar homologar un deposito de fondo.</summary>
public sealed class ResultadoHomologacionFondo
{
    public FondoHomologado? Fondo { get; init; }
    public List<string> Errores { get; init; } = new();
    public bool Ok => Errores.Count == 0 && Fondo is not null;
}

/// <summary>
/// Lo propio de las SOLICITUDES de fondo: alguien pide dinero por adelantado y,
/// al aprobarse, se le transfiere. Contabilidad lo carga a entregas a rendir,
/// igual para las dos politicas ("Solicitud Viaticos" y "Solicitud Entrega a
/// Rendir"), como en el comprobante 2606190 que ingreso a mano:
///
///   tipo 19, folio 7, glosa "TR-7 LILIAN JOSE - VIATICOS PIURA"
///     1413110  analisis=31909 (DNI 42791075)  td=26  num=16092026  cargo 500.00
///     1041060  analisis=765 (BCP)             td=55  num=TR-7      abono 500.00
/// </summary>
public static class ConstantesSolicitudFondo
{
    /// <summary>Estado de la solicitud que se integra.</summary>
    public const string Aprobada = "APPROVED";

    /// <summary>
    /// Cuenta de destino: la misma que usan las rendiciones de entrega a rendir
    /// y viaticos, en soles o en dolares.
    /// </summary>
    public static string CuentaPara(byte codMoneda)
        => codMoneda == ConstantesErp.MonedaSoles
            ? ConstantesRendicion.CuentaEntregaARendir
            : ConstantesRendicion.CuentaEntregaARendirME;
}

/// <summary>Estados de rg_fondo.rgf_estado. Mismos significados que rg_gasto.</summary>
public static class EstadoFondo
{
    public const byte Descargado = 0;
    public const byte Homologado = 1;
    public const byte Contabilizado = 2;
    public const byte Confirmado = 3;
    public const byte Error = 9;
}
