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
}

/// <summary>
/// Bloque Taxes. Los porcentajes llegan como texto ("18.00"), por eso son string.
/// Los montos son informativos: el ERP recalcula segun su propia tasa de IGV.
/// </summary>
public sealed class ImpuestosApi
{
    public string? taxPercentage { get; set; }
    public string? taxName { get; set; }
    public decimal taxAmount { get; set; }
    public decimal tax { get; set; }
    public decimal otherTaxes { get; set; }
    public decimal retention { get; set; }
}

/// <summary>Validacion SUNAT del comprobante. Respaldo del RUC y la razon social.</summary>
public sealed class SunatApi
{
    public string? Ruc { get; set; }
    public string? BusinessName { get; set; }
    public string? TaxpayerStatus { get; set; }
    public string? DocStatusName { get; set; }
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

/// <summary>Cuerpo de PUT /setExpenseIntegrationBulk.</summary>
public sealed class MarcaIntegracion
{
    public long Id { get; set; }
    public int IntegrationStatus { get; set; } = 1;
    public string IntegrationCode { get; set; } = "";
    public string IntegrationDate { get; set; } = "";
}
