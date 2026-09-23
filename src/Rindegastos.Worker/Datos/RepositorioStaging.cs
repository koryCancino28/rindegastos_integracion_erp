using System.Text.Json;
using Microsoft.Data.SqlClient;
using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Modelo;

namespace Rindegastos.Worker.Datos;

/// <summary>Lee y escribe la tabla rg_gasto (staging).</summary>
public sealed class RepositorioStaging
{
    private readonly FabricaConexion _fabrica;
    private readonly ILogger<RepositorioStaging> _logger;

    public RepositorioStaging(FabricaConexion fabrica, ILogger<RepositorioStaging> logger)
    {
        _fabrica = fabrica;
        _logger = logger;
    }

    /// <summary>Devuelve los Id de Rindegastos que ya estan en staging, para no reprocesarlos.</summary>
    public async Task<HashSet<long>> ObtenerIdsExistentesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var existentes = new HashSet<long>();
        var lista = ids.ToList();
        if (lista.Count == 0) return existentes;

        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        // Se arma la lista de parametros: los Id son numericos y provienen de la API, nunca del usuario.
        var nombres = lista.Select((_, i) => "@p" + i).ToList();
        cmd.CommandText = $"SELECT rgg_id FROM dbo.rg_gasto WHERE rgg_id IN ({string.Join(",", nombres)})";
        for (var i = 0; i < lista.Count; i++) cmd.Parameters.AddWithValue("@p" + i, lista[i]);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) existentes.Add(rd.GetInt64(0));
        return existentes;
    }

    /// <summary>Inserta el gasto en staging tal como llego de la API, en estado DESCARGADO.</summary>
    public async Task InsertarAsync(GastoApi g, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.rg_gasto
 (rgg_id, rgg_report_id, rgg_user_id, rgg_policy_id, rgg_status, rgg_supplier, rgg_issue_date,
  rgg_total, rgg_net_api, rgg_tax_api, rgg_tax_percentage, rgg_currency, rgg_exchange_rate,
  rgg_category, rgg_category_code, rgg_note, rgg_reimbursable,
  rgg_ruc_proveedor, rgg_tipo_doc_code, rgg_tipo_doc_nombre, rgg_nro_documento,
  rgg_centro_costo_code, rgg_centro_costo_nombre,
  rgg_sunat_ruc, rgg_sunat_razon_social, rgg_sunat_doc_estado, rgg_json, rgg_estado)
VALUES
 (@id, @report, @user, @policy, @status, @supplier, @issue,
  @total, @net, @tax, @taxPct, @cur, @rate,
  @cat, @catCode, @note, @reimb,
  @ruc, @tdCode, @tdNombre, @nroDoc,
  @ccCode, @ccNombre,
  @sunatRuc, @sunatRazon, @sunatEstado, @json, @estado);";

        CargarParametros(cmd, g);
        cmd.Parameters.AddWithValue("@estado", EstadoGasto.Descargado);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Actualiza en staging los gastos que todavia NO se contabilizaron (estados
    /// 0, 1 y 9) con lo que acaba de devolver la API.
    ///
    /// Sin esto, el worker trabajaria para siempre con la primera version del
    /// gasto: si alguien corrige el RUC o la cuenta en Rindegastos, la correccion
    /// nunca llegaria. Si el gasto cambio, vuelve a pendiente (estado 0, 0 intentos)
    /// para que se procese de nuevo con los datos corregidos.
    ///
    /// Los estados 2 y 3 no se tocan nunca: ya tienen comprobante en el ERP.
    /// </summary>
    /// <returns>Cuantos gastos cambiaron.</returns>
    public async Task<int> RefrescarPendientesAsync(IEnumerable<GastoApi> gastos, CancellationToken ct)
    {
        var cambiados = 0;
        await using var cn = await _fabrica.AbrirAsync(ct);

        foreach (var g in gastos)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.rg_gasto SET
  rgg_report_id = @report, rgg_user_id = @user, rgg_policy_id = @policy, rgg_status = @status,
  rgg_supplier = @supplier, rgg_issue_date = @issue,
  rgg_total = @total, rgg_net_api = @net, rgg_tax_api = @tax, rgg_tax_percentage = @taxPct,
  rgg_currency = @cur, rgg_exchange_rate = @rate,
  rgg_category = @cat, rgg_category_code = @catCode, rgg_note = @note, rgg_reimbursable = @reimb,
  rgg_ruc_proveedor = @ruc, rgg_tipo_doc_code = @tdCode, rgg_tipo_doc_nombre = @tdNombre,
  rgg_nro_documento = @nroDoc, rgg_centro_costo_code = @ccCode, rgg_centro_costo_nombre = @ccNombre,
  rgg_sunat_ruc = @sunatRuc, rgg_sunat_razon_social = @sunatRazon, rgg_sunat_doc_estado = @sunatEstado,
  rgg_json = @json,
  rgg_estado = 0, rgg_intentos = 0, rgg_ultimo_error = NULL
WHERE rgg_id = @id
  AND rgg_estado IN (0, 1, 9)
  AND (rgg_json IS NULL OR rgg_json <> @json);";

            CargarParametros(cmd, g);
            if (await cmd.ExecuteNonQueryAsync(ct) > 0) cambiados++;
        }

        return cambiados;
    }

    /// <summary>Parametros comunes del insert y del refresco.</summary>
    private static void CargarParametros(SqlCommand cmd, GastoApi g)
    {
        var ruc = g.CampoExtra("Ruc Proveedor");
        var tipoDoc = g.CampoExtra("Tipo de Documento");
        var nroDoc = g.CampoExtra("Nro Documento");
        var centro = g.CampoExtra("Centro de Costos 1");

        cmd.Parameters.AddWithValue("@id", g.Id);
        cmd.Parameters.AddWithValue("@report", g.ReportId);
        cmd.Parameters.AddWithValue("@user", g.UserId);
        cmd.Parameters.AddWithValue("@policy", g.ExpensePolicyId);
        cmd.Parameters.AddWithValue("@status", (byte)g.Status);
        cmd.Parameters.AddWithValue("@supplier", Val(g.Supplier));
        cmd.Parameters.AddWithValue("@issue", (object?)g.IssueDate ?? DBNull.Value);
        // Se guarda OriginalAmount / OriginalCurrency, que es el documento tal como
        // se emitio. Total y Currency vienen convertidos a la moneda de la politica
        // y llegan en 0 cuando Rindegastos no tiene tipo de cambio configurado.
        cmd.Parameters.AddWithValue("@total", g.OriginalAmount);
        cmd.Parameters.AddWithValue("@net", g.Net);
        cmd.Parameters.AddWithValue("@tax", g.Taxes?.tax ?? 0m);
        cmd.Parameters.AddWithValue("@taxPct", ParsearDecimal(g.Taxes?.taxPercentage));
        cmd.Parameters.AddWithValue("@cur", Val(g.OriginalCurrency));
        cmd.Parameters.AddWithValue("@rate", g.ExchangeRate);
        cmd.Parameters.AddWithValue("@cat", Val(g.Category));
        cmd.Parameters.AddWithValue("@catCode", Val(g.CategoryCode));
        cmd.Parameters.AddWithValue("@note", Val(g.Note));
        cmd.Parameters.AddWithValue("@reimb", g.Reimbursable);
        cmd.Parameters.AddWithValue("@ruc", Val(ruc?.Value));
        cmd.Parameters.AddWithValue("@tdCode", Val(tipoDoc?.Code));
        cmd.Parameters.AddWithValue("@tdNombre", Val(tipoDoc?.Value));
        cmd.Parameters.AddWithValue("@nroDoc", Val(nroDoc?.Value));
        cmd.Parameters.AddWithValue("@ccCode", Val(centro?.Code));
        cmd.Parameters.AddWithValue("@ccNombre", Val(centro?.Value));
        cmd.Parameters.AddWithValue("@sunatRuc", Val(g.SunatInfo?.Ruc));
        cmd.Parameters.AddWithValue("@sunatRazon", Val(g.SunatInfo?.BusinessName));
        cmd.Parameters.AddWithValue("@sunatEstado", Val(g.SunatInfo?.DocStatusName));
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(g));
    }

    /// <summary>Guarda los codigos y montos resueltos por la homologacion.</summary>
    public async Task GuardarHomologacionAsync(GastoHomologado g, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_gasto SET
    rgg_cod_proveedor    = @prov,
    rgg_cod_analisis     = @ana,
    rgg_cod_tipo_doc     = @tdoc,
    rgg_cod_moneda       = @mon,
    rgg_tipo_cambio      = @tc,
    rgg_cod_centro_costo = @cc,
    rgg_cod_plan_cuenta  = @cuenta,
    rgg_monto_neto       = @neto,
    rgg_monto_igv        = @igv,
    rgg_monto_exento     = @exento,
    rgg_estado           = @estado,
    rgg_ultimo_error     = NULL
WHERE rgg_id = @id;";
        cmd.Parameters.AddWithValue("@prov", g.CodProveedor);
        cmd.Parameters.AddWithValue("@ana", g.CodAnalisis);
        cmd.Parameters.AddWithValue("@tdoc", g.CodTipoDocumento);
        cmd.Parameters.AddWithValue("@mon", g.CodMoneda);
        cmd.Parameters.AddWithValue("@tc", g.TipoCambio);
        cmd.Parameters.AddWithValue("@cc", (object?)g.CodCentroCosto ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cuenta", g.CodPlanCuentaGasto);
        cmd.Parameters.AddWithValue("@neto", g.MontoNeto);
        cmd.Parameters.AddWithValue("@igv", g.MontoIgv);
        cmd.Parameters.AddWithValue("@exento", g.MontoExento);
        cmd.Parameters.AddWithValue("@estado", EstadoGasto.Homologado);
        cmd.Parameters.AddWithValue("@id", g.IdRindegastos);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Marca el gasto como contabilizado y guarda los codigos generados en el ERP.</summary>
    public async Task MarcarContabilizadoAsync(long idRg, int codFactura, int codComprobante, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_gasto SET
    rgg_estado              = @estado,
    rgg_cod_factura_boleta  = @factura,
    rgg_cod_comprobante     = @comprobante,
    rgg_fecha_contabilizado = GETDATE(),
    rgg_ultimo_error        = NULL
WHERE rgg_id = @id;";
        cmd.Parameters.AddWithValue("@estado", EstadoGasto.Contabilizado);
        cmd.Parameters.AddWithValue("@factura", codFactura);
        cmd.Parameters.AddWithValue("@comprobante", codComprobante);
        cmd.Parameters.AddWithValue("@id", idRg);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Marca el gasto como confirmado en Rindegastos. Es el fin del flujo.</summary>
    public async Task MarcarConfirmadoAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var lista = ids.ToList();
        if (lista.Count == 0) return;

        await using var cn = await _fabrica.AbrirAsync(ct);
        foreach (var id in lista)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.rg_gasto
SET rgg_estado = @estado, rgg_fecha_confirmado = GETDATE()
WHERE rgg_id = @id;";
            cmd.Parameters.AddWithValue("@estado", EstadoGasto.Confirmado);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Registra un error y, si se agotaron los intentos, deja el gasto en estado ERROR.</summary>
    public async Task RegistrarErrorAsync(long idRg, string error, int maxIntentos, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.rg_gasto SET
    rgg_intentos     = rgg_intentos + 1,
    rgg_ultimo_error = @error,
    rgg_estado       = CASE WHEN rgg_intentos + 1 >= @max THEN @estadoError ELSE rgg_estado END
WHERE rgg_id = @id;";
        cmd.Parameters.AddWithValue("@error", error.Length > 2000 ? error[..2000] : error);
        cmd.Parameters.AddWithValue("@max", maxIntentos);
        cmd.Parameters.AddWithValue("@estadoError", EstadoGasto.Error);
        cmd.Parameters.AddWithValue("@id", idRg);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Devuelve los Id de gastos que estan en un estado determinado.</summary>
    public async Task<List<long>> ObtenerIdsPorEstadoAsync(byte estado, CancellationToken ct)
    {
        var ids = new List<long>();
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgg_id FROM dbo.rg_gasto WHERE rgg_estado = @estado ORDER BY rgg_id";
        cmd.Parameters.AddWithValue("@estado", estado);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetInt64(0));
        return ids;
    }

    /// <summary>Datos necesarios para confirmar la integracion en Rindegastos.</summary>
    public async Task<List<MarcaIntegracion>> ObtenerPendientesDeConfirmarAsync(CancellationToken ct)
    {
        var marcas = new List<MarcaIntegracion>();
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT rgg_id, rgg_cod_comprobante, rgg_fecha_contabilizado
FROM dbo.rg_gasto
WHERE rgg_estado = @estado AND rgg_cod_comprobante IS NOT NULL
ORDER BY rgg_id;";
        cmd.Parameters.AddWithValue("@estado", EstadoGasto.Contabilizado);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var fecha = rd.IsDBNull(2) ? DateTime.Today : rd.GetDateTime(2);
            marcas.Add(new MarcaIntegracion
            {
                Id = rd.GetInt64(0),
                IntegrationStatus = 1,
                IntegrationCode = rd.GetInt32(1).ToString(),
                IntegrationDate = fecha.ToString("yyyy-MM-dd")
            });
        }
        return marcas;
    }

    /// <summary>Lee un gasto de staging y lo devuelve deserializado desde su JSON original.</summary>
    public async Task<GastoApi?> ObtenerGastoAsync(long idRg, CancellationToken ct)
    {
        await using var cn = await _fabrica.AbrirAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT rgg_json FROM dbo.rg_gasto WHERE rgg_id = @id";
        cmd.Parameters.AddWithValue("@id", idRg);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json)) return null;

        return JsonSerializer.Deserialize<GastoApi>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static object Val(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();

    private static object ParsearDecimal(string? s)
        => decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d)
           ? d : DBNull.Value;
}
