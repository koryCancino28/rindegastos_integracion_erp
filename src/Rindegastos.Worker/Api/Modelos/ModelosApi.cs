using System.Text.Json.Serialization;

namespace Rindegastos.Worker.Api.Modelos;

/// <summary>Bloque "Records" que envuelve todas las respuestas de la API.</summary>
public sealed class Records
{
    public int TotalRecords { get; set; }
    public int Page { get; set; }
    public int Pages { get; set; }
    public string? ResponseMessage { get; set; }
}

/// <summary>Respuesta de GET /getExpenses.</summary>
public sealed class RespuestaGastos
{
    public Records? Records { get; set; }
    public List<GastoApi> Expenses { get; set; } = new();
}

/// <summary>Un gasto tal como lo devuelve Rindegastos.</summary>
public sealed class GastoApi
{
    public long Id { get; set; }
    public int Status { get; set; }
    public string? Supplier { get; set; }
    public DateTime? IssueDate { get; set; }
    public decimal OriginalAmount { get; set; }
    public string? OriginalCurrency { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal Net { get; set; }
    public ImpuestosApi? Taxes { get; set; }
    public decimal Total { get; set; }
    public string? Currency { get; set; }
    public bool Reimbursable { get; set; }
    public string? Category { get; set; }
    public string? CategoryCode { get; set; }
    public string? CategoryGroup { get; set; }
    public string? CategoryGroupCode { get; set; }
    public long ReportId { get; set; }
    public long ExpensePolicyId { get; set; }
    public long UserId { get; set; }
    public string? Note { get; set; }
    public bool IsIntegrated { get; set; }
    public SunatApi? SunatInfo { get; set; }
    public List<CampoExtraApi> ExtraFields { get; set; } = new();

    /// <summary>Busca un campo extra por nombre exacto (sin distinguir mayusculas).</summary>
    public CampoExtraApi? CampoExtra(string nombre) =>
        ExtraFields.FirstOrDefault(c => string.Equals(c.Name?.Trim(), nombre, StringComparison.OrdinalIgnoreCase));

    /// <summary>Code del campo extra "Tipo de Documento" ("01", "03", "R1", "PL"...).</summary>
    public string? TipoDocumentoCode => CampoExtra("Tipo de Documento")?.Code?.Trim();

    /// <summary>
    /// true cuando el campo extra "¿Es un gasto parcial?" dice que si.
    ///
    /// Un gasto parcial es un documento del que la empresa asume solo una parte
    /// y el resto se le cobra al trabajador. Cambia el significado de los montos:
    ///
    ///   OriginalAmount            lo que asume la empresa   (30.00)
    ///   campo extra "Monto total" el total del documento    (36.00)
    ///   la diferencia             lo que paga el trabajador  (6.00)
    ///
    /// Llega como texto libre ("Si" / "No"), sin Code, asi que se compara sin
    /// tildes ni mayusculas.
    /// </summary>
    public bool EsGastoParcial
    {
        get
        {
            var v = CampoExtra("¿Es un gasto parcial?")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(v)) return false;
            return v.StartsWith("s", StringComparison.OrdinalIgnoreCase);   // "Si", "SI", "Sí"
        }
    }

    /// <summary>
    /// Campo extra "¿Es factura no domiciliada?" (llega con un espacio al final
    /// del nombre; CampoExtra ya lo recorta). Solo sirve para validar: lo que
    /// decide el trato de no domiciliado es el tipo de documento 37, igual que
    /// en la pantalla.
    /// </summary>
    public bool DiceNoDomiciliada
    {
        get
        {
            var v = CampoExtra("¿Es factura no domiciliada?")?.Value?.Trim();
            return !string.IsNullOrWhiteSpace(v) && v.StartsWith("s", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Campo extra "Monto total": el total real del documento cuando el gasto es
    /// parcial. Devuelve null si no viene o no es un numero.
    /// </summary>
    public decimal? MontoTotalDocumento
    {
        get
        {
            var v = CampoExtra("Monto total")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(v)) return null;

            // Llega como "36.00", con punto decimal, sin importar la cultura del
            // servidor: por eso se parsea con InvariantCulture.
            return decimal.TryParse(v, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n : null;
        }
    }

    /// <summary>
    /// true si el gasto es una planilla de movilidad. Esos van por el flujo de
    /// rendiciones (Ingreso de Comprobante); el resto va por el de compras.
    /// </summary>
    public bool EsPlanillaMovilidad =>
        string.Equals(TipoDocumentoCode, Modelo.ConstantesRendicion.TipoDocumentoPlanillaMovilidad,
                      StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Bloque Taxes. Los porcentajes llegan como texto ("18.00"), por eso son string.
///
/// El IGV que manda Rindegastos MANDA sobre la tasa del ERP: quien rinde elige
/// el impuesto del documento, y puede no ser 18% ("IGV 10.5%" en el gasto
/// 79222741, factura FAA1-32729996). Solo se usa en facturas.
/// </summary>
public sealed class ImpuestosApi
{
    public string? taxPercentage { get; set; }
    public string? taxName { get; set; }
    public decimal taxAmount { get; set; }
    public decimal tax { get; set; }
    public decimal otherTaxes { get; set; }
    public decimal retention { get; set; }

    /// <summary>
    /// taxPercentage como numero. Llega como texto y con punto decimal, asi que
    /// se lee en formato invariante. null si no viene o no es un numero.
    /// </summary>
    public decimal? Porcentaje =>
        decimal.TryParse(taxPercentage, System.Globalization.NumberStyles.Any,
                         System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}

/// <summary>
/// Validacion SUNAT que hace Rindegastos. Da el RUC y la razon social del
/// proveedor, y su situacion tributaria.
/// </summary>
public sealed class SunatApi
{
    public string? Ruc { get; set; }
    public string? BusinessName { get; set; }
    public string? TaxpayerStatus { get; set; }
    public string? AddressCondition { get; set; }
    public string? DocStatusName { get; set; }

    /// <summary>Estado del contribuyente segun SUNAT: "ACTIVO", "BAJA DE OFICIO"...</summary>
    public string? DocTaxpayerStatus { get; set; }

    /// <summary>Condicion del domicilio fiscal segun SUNAT: "HABIDO", "NO HABIDO"...</summary>
    public string? DocTaxpayerAddressCondition { get; set; }

    /// <summary>
    /// Detalle de la validacion del comprobante, como texto JSON. Trae el campo
    /// "observations" con el motivo cuando la validacion falla (por ejemplo,
    /// "El comprobante de pago consultado ha sido emitido a otro contribuyente").
    /// </summary>
    public string? DocExtractedData { get; set; }

    /// <summary>Observacion de SUNAT sobre el comprobante, si la hay.</summary>
    public string? ObservacionComprobante
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DocExtractedData)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(DocExtractedData);
                return doc.RootElement.TryGetProperty("observations", out var o) &&
                       o.ValueKind == System.Text.Json.JsonValueKind.String
                    ? o.GetString()?.Trim().TrimStart('-').Trim()
                    : null;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
    }

    /// <summary>
    /// Datos del padron de contribuyentes (consulta a migo), como texto JSON.
    /// Trae "taxPayerStatus" y "condition" aunque la validacion del comprobante
    /// haya fallado.
    /// </summary>
    public string? ExtractedData { get; set; }

    /// <summary>
    /// Estado del contribuyente para decidir el alta. Se toma DocTaxpayerStatus;
    /// si viene vacio, el "taxPayerStatus" de ExtractedData.
    ///
    /// DocTaxpayerStatus viene vacio cuando falla la validacion del comprobante
    /// (por ejemplo "NO EXISTE - Comprobante no informado"), pero el padron sigue
    /// diciendo si el contribuyente esta activo. Si DocTaxpayerStatus trae un
    /// valor, ese manda.
    /// </summary>
    public string? EstadoContribuyente =>
        NoVacio(DocTaxpayerStatus) ?? NoVacio(LeerDelPadron("taxPayerStatus"));

    /// <summary>Condicion del domicilio: DocTaxpayerAddressCondition, o "condition" de ExtractedData si viene vacio.</summary>
    public string? CondicionDomicilio =>
        NoVacio(DocTaxpayerAddressCondition) ?? NoVacio(LeerDelPadron("condition"));

    /// <summary>true si el estado y la condicion salieron de la validacion del comprobante (Doc*).</summary>
    public bool EstadoDeLaValidacion =>
        NoVacio(DocTaxpayerStatus) is not null && NoVacio(DocTaxpayerAddressCondition) is not null;

    /// <summary>De donde salio el estado usado, para los mensajes.</summary>
    public string OrigenEstado =>
        EstadoDeLaValidacion ? "la validacion del comprobante" : "el padron de contribuyentes";

    /// <summary>
    /// true si el contribuyente esta ACTIVO y HABIDO. Es la condicion que puso
    /// Contabilidad para dar de alta un proveedor automaticamente.
    /// </summary>
    public bool ActivoYHabido =>
        string.Equals(EstadoContribuyente, "ACTIVO", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(CondicionDomicilio, "HABIDO", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lee un campo del JSON de ExtractedData. Si no se puede leer, devuelve null.</summary>
    private string? LeerDelPadron(string campo)
    {
        if (string.IsNullOrWhiteSpace(ExtractedData)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ExtractedData);
            return doc.RootElement.TryGetProperty(campo, out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? NoVacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Campo extra de la politica de gastos.
/// Id puede llegar como numero o como texto segun el endpoint, por eso es string.
/// </summary>
public sealed class CampoExtraApi
{
    [JsonConverter(typeof(ConvertidorTextoFlexible))]
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? Code { get; set; }
}

/// <summary>
/// Cuerpo de PUT /setExpenseIntegrationBulk.
///
/// OJO CON IntegrationStatus: va como TEXTO ("1"), no como numero. Con numero la
/// API contesta HTTP 200 con {"statusCode":500,"message":"Internal server error"}
/// adentro y no marca nada. Verificado el 24/09/2026 contra los cuatro metodos.
/// </summary>
public sealed class MarcaIntegracion
{
    public long Id { get; set; }
    public string IntegrationStatus { get; set; } = ConstantesMarcaIntegracion.Integrado;
    public string IntegrationCode { get; set; } = "";
    public string IntegrationDate { get; set; } = "";
}

/// <summary>Valores de IntegrationStatus que acepta la API.</summary>
public static class ConstantesMarcaIntegracion
{
    public const string Integrado = "1";
    public const string NoIntegrado = "0";
}

// ===========================================================================
// Informes de gastos (rendiciones)
// ---------------------------------------------------------------------------
// Un informe agrupa varios gastos. El campo extra "Tipo de rendicion" del
// informe decide como se contabiliza: entrega a rendir, viaticos, reembolso
// o caja chica. El enlace entre gasto e informe es GastoApi.ReportId, que
// vale lo mismo que InformeApi.Id; ademas getExpenses acepta ReportId como
// filtro, asi que se pueden pedir todos los gastos de un informe de una vez.
// ===========================================================================

/// <summary>Respuesta de GET /getExpenseReports.</summary>
public sealed class RespuestaInformes
{
    public Records? Records { get; set; }
    public List<InformeApi> ExpenseReports { get; set; } = new();
}

/// <summary>Un informe de gastos tal como lo devuelve Rindegastos.</summary>
public sealed class InformeApi
{
    public long Id { get; set; }
    public string? Title { get; set; }
    public string? ReportNumber { get; set; }
    public DateTime? SentDate { get; set; }
    public DateTime? ClosedDate { get; set; }
    public EmpleadoApi? Employee { get; set; }
    public int Status { get; set; }

    /// <summary>Id del fondo de caja chica. Solo viene en informes de caja chica.</summary>
    public long? FundId { get; set; }
    public string? FundName { get; set; }

    public decimal ReportTotal { get; set; }
    public string? Currency { get; set; }
    public string? Note { get; set; }
    public int NumberExpenses { get; set; }

    /// <summary>0 = no integrado todavia. Llega como numero, no como booleano.</summary>
    public int IsIntegrated { get; set; }

    public List<CampoExtraApi> ExtraFields { get; set; } = new();

    /// <summary>Busca un campo extra por nombre exacto (sin distinguir mayusculas).</summary>
    public CampoExtraApi? CampoExtra(string nombre) =>
        ExtraFields.FirstOrDefault(c => string.Equals(c.Name?.Trim(), nombre, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Valor del campo extra "Tipo de rendicion". Llega siempre con Code vacio,
    /// asi que hay que homologar por texto.
    /// </summary>
    public string? TipoRendicionTexto => CampoExtra("Tipo de rendición")?.Value
                                      ?? CampoExtra("Tipo de rendicion")?.Value;
}

/// <summary>Persona que envia el informe.</summary>
public sealed class EmpleadoApi
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }

    /// <summary>Documento de identidad. Suele llegar vacio.</summary>
    public string? Identification { get; set; }
    public string? Department { get; set; }
}

/// <summary>Cuerpo de PUT /setExpenseReportIntegrationBulk.</summary>
public sealed class MarcaIntegracionInforme
{
    public long Id { get; set; }
    public string IntegrationStatus { get; set; } = ConstantesMarcaIntegracion.Integrado;
    public string IntegrationCode { get; set; } = "";
    public string IntegrationDate { get; set; } = "";
}

// ---------------------------------------------------------------- fondos

/// <summary>Respuesta de GET /getFunds.</summary>
public sealed class RespuestaFondos
{
    public Records? Records { get; set; }
    public List<FondoApi> Funds { get; set; } = new();
}

/// <summary>
/// Un fondo de Rindegastos (caja chica, viaticos por rendir, etc.).
///
/// Contabilidad acordo usar dos campos del fondo para decir como contabilizarlo:
///   Description = codigo de la cuenta contable del ERP (por ejemplo 1020152)
///   Code        = documento de identidad de la persona que recibe el fondo
/// </summary>
public sealed class FondoApi
{
    public long Id { get; set; }
    public string? Title { get; set; }

    /// <summary>Documento de identidad de quien recibe el fondo.</summary>
    public string? Code { get; set; }

    /// <summary>Codigo de la cuenta contable del ERP donde se carga el fondo.</summary>
    public string? Description { get; set; }

    public string? Currency { get; set; }
    public decimal Deposits { get; set; }
    public decimal Withdrawals { get; set; }
    public decimal Balance { get; set; }

    /// <summary>1 = activo, 2 = cerrado.</summary>
    public int Status { get; set; }

    /// <summary>0 = todavia no integrado en el ERP.</summary>
    public int IsIntegrated { get; set; }

    public DateTime? CreatedAt { get; set; }
    public long? IdAssignTo { get; set; }

    /// <summary>
    /// Id de la solicitud de fondo que lo creo, si nacio de una. Esos fondos NO
    /// entran por el flujo de fondos: los registra el flujo de solicitudes, que
    /// es el que tiene el DNI y el tipo de rendicion.
    /// </summary>
    public string? FundRequestId { get; set; }

    /// <summary>true si el fondo nacio de una solicitud aprobada.</summary>
    public bool VieneDeSolicitud => !string.IsNullOrWhiteSpace(FundRequestId);

    public List<TransaccionFondoApi> Transactions { get; set; } = new();

    /// <summary>
    /// Los depositos del fondo, en orden. Cada uno es una entrega de dinero y
    /// genera su propio comprobante de transferencia en el ERP. Los retiros
    /// (TransactionType 3 y 4) son las liquidaciones, que entran por el flujo
    /// de informes.
    /// </summary>
    public List<TransaccionFondoApi> Depositos => Transactions
        .Where(t => t.TransactionType == 1)
        .OrderBy(t => t.TransactionDate)
        .ThenBy(t => t.CreatedAt)
        .ToList();
}

/// <summary>Un movimiento del fondo: deposito (1) o retiro (3 y 4).</summary>
public sealed class TransaccionFondoApi
{
    public long? ReportId { get; set; }
    public int TransactionType { get; set; }
    public string? TransactionTypeName { get; set; }
    public decimal TransactionAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public DateTime? TransactionDate { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>Cuerpo de PUT /setFundIntegrationBulk.</summary>
public sealed class MarcaIntegracionFondo
{
    public long Id { get; set; }
    public string IntegrationStatus { get; set; } = ConstantesMarcaIntegracion.Integrado;
    public string IntegrationCode { get; set; } = "";
    public string IntegrationDate { get; set; } = "";
}

// ------------------------------------------------- solicitudes de fondo

/// <summary>Respuesta de GET /getFundsRequest.</summary>
public sealed class RespuestaSolicitudesFondo
{
    public Records? Records { get; set; }
    public List<SolicitudFondoApi> FundsRequest { get; set; } = new();
}

/// <summary>
/// Una solicitud de fondo: alguien pide dinero por adelantado (viaticos o
/// entrega a rendir) y, cuando se aprueba, Rindegastos le crea un fondo.
/// El Id NO es numerico: es un identificador de 24 caracteres.
/// </summary>
public sealed class SolicitudFondoApi
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Currency { get; set; }
    public decimal Amount { get; set; }

    /// <summary>APPROVED cuando termino de aprobarse. Solo esas se integran.</summary>
    public string? Status { get; set; }

    public long EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public string? ApproverName { get; set; }

    /// <summary>Fondo que Rindegastos creo al aprobarla.</summary>
    public long? FundId { get; set; }

    public string? PolicyId { get; set; }
    public string? PolicyName { get; set; }

    public DateTime? SentDate { get; set; }

    /// <summary>Fecha de aprobacion. Es la fecha del comprobante.</summary>
    public DateTime? ClosedDate { get; set; }

    /// <summary>false = todavia no integrada en el ERP.</summary>
    public bool IsIntegration { get; set; }
    public string? IntegrationCode { get; set; }

    public List<CampoExtraApi> ExtraFields { get; set; } = new();

    public CampoExtraApi? CampoExtra(string nombre) =>
        ExtraFields.FirstOrDefault(c => string.Equals(c.Name?.Trim(), nombre, StringComparison.OrdinalIgnoreCase));

    /// <summary>Documento de quien recibe el dinero. Campo extra "DNI".</summary>
    public string? Dni => CampoExtra("DNI")?.Value?.Trim();

    /// <summary>"Solicitud Viaticos" o "Solicitud Entrega a Rendir".</summary>
    public string? TipoRendicionTexto => CampoExtra("Tipo de rendición")?.Value
                                      ?? CampoExtra("Tipo de rendicion")?.Value;

    public bool Aprobada => string.Equals(Status?.Trim(), "APPROVED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Cuerpo de PUT /setFundRequestIntegrationBulk.
///
/// Aqui la solicitud NO se identifica con "Id" sino con "FundRequestId": es el
/// unico de los cuatro metodos que cambia el nombre de la llave.
/// </summary>
public sealed class MarcaIntegracionSolicitud
{
    public string FundRequestId { get; set; } = "";
    public string IntegrationStatus { get; set; } = ConstantesMarcaIntegracion.Integrado;
    public string IntegrationCode { get; set; } = "";
    public string IntegrationDate { get; set; } = "";
}
