using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Rindegastos.Worker.Api.Modelos;
using Rindegastos.Worker.Configuracion;
using Rindegastos.Worker.Datos;

namespace Rindegastos.Worker.Api;

/// <summary>
/// Cliente HTTP de la API v2 de Rindegastos.
/// Solo consume los metodos que necesita el proceso de Gastos:
///   GET  /getExpenses                 -> descarga los gastos por contabilizar
///   PUT  /setExpenseIntegrationBulk   -> devuelve el numero de comprobante del ERP
/// </summary>
public sealed class ClienteRindegastos
{
    private readonly HttpClient _http;
    private readonly OpcionesRindegastos _opciones;
    private readonly RepositorioLog _log;
    private readonly ILogger<ClienteRindegastos> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public ClienteRindegastos(
        HttpClient http,
        IOptions<OpcionesRindegastos> opciones,
        RepositorioLog log,
        ILogger<ClienteRindegastos> logger)
    {
        _opciones = opciones.Value;
        _log = log;
        _logger = logger;

        _http = http;
        _http.BaseAddress = new Uri(_opciones.UrlBase.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opciones.TimeoutSegundos);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opciones.Token);
    }

    /// <summary>
    /// Descarga TODOS los gastos que cumplen el filtro, recorriendo todas las paginas.
    /// </summary>
    /// <param name="status">1 = Aprobado. null = todos.</param>
    /// <param name="integrationStatus">0 = aun no integrados en el ERP.</param>
    /// <param name="desde">Fecha minima de emision (yyyy-MM-dd).</param>
    public async Task<List<GastoApi>> ObtenerGastosAsync(
        int? status, int? integrationStatus, string? desde, CancellationToken ct)
    {
        var todos = new List<GastoApi>();
        var pagina = 1;

        while (true)
        {
            var query = new List<string> { $"ResultsPerPage={_opciones.ResultadosPorPagina}", $"Page={pagina}" };
            if (status is not null)            query.Add($"Status={status}");
            if (integrationStatus is not null) query.Add($"IntegrationStatus={integrationStatus}");
            if (!string.IsNullOrWhiteSpace(desde)) query.Add($"Since={desde}");

            var url = "getExpenses?" + string.Join("&", query);
            var resp = await EnviarAsync<RespuestaGastos>(HttpMethod.Get, url, null, "getExpenses", ct);

            if (resp?.Expenses is null || resp.Expenses.Count == 0) break;

            todos.AddRange(resp.Expenses);
            _logger.LogInformation("getExpenses pagina {Pagina}/{Total}: {N} gastos",
                pagina, resp.Records?.Pages ?? 1, resp.Expenses.Count);

            if (resp.Records is null || pagina >= resp.Records.Pages) break;
            pagina++;
        }

        return todos;
    }

    /// <summary>
    /// Marca gastos como integrados. La API acepta hasta 100 por llamada,
    /// asi que la lista se parte automaticamente en bloques de 100.
    ///
    /// El cuerpo NO es una lista suelta: es un objeto con la lista adentro,
    /// {"Expenses":[...]}. Con la lista suelta la API contesta
    /// "property 0 should not exist". Lo mismo en informes (ExpenseReports),
    /// fondos (Funds) y solicitudes (FundsRequest).
    /// </summary>
    public async Task MarcarGastosIntegradosAsync(IReadOnlyList<MarcaIntegracion> marcas, CancellationToken ct)
    {
        foreach (var bloque in marcas.Chunk(100))
        {
            await EnviarAsync<JsonElement>(
                HttpMethod.Put, "setExpenseIntegrationBulk", new { Expenses = bloque },
                "setExpenseIntegrationBulk", ct);

            _logger.LogInformation("setExpenseIntegrationBulk: {N} gastos marcados", bloque.Length);
        }
    }

    // ------------------------------------------------------------- informes

    /// <summary>
    /// Descarga los informes de gastos (rendiciones) recorriendo todas las paginas.
    /// Un informe agrupa varios gastos y su campo extra "Tipo de rendicion" decide
    /// como se contabiliza en el ERP.
    /// </summary>
    /// <param name="status">
    /// OJO: en informes Status NO significa lo mismo que en gastos.
    /// Aqui 0 = Abierto o En proceso y 1 = Cerrado. null = ambos.
    /// </param>
    /// <param name="integrationStatus">0 = aun no integrados en el ERP.</param>
    /// <param name="desde">Fecha minima (yyyy-MM-dd).</param>
    public async Task<List<InformeApi>> ObtenerInformesAsync(
        int? status, int? integrationStatus, string? desde, CancellationToken ct)
    {
        var todos = new List<InformeApi>();
        var pagina = 1;

        while (true)
        {
            var query = new List<string> { $"ResultsPerPage={_opciones.ResultadosPorPagina}", $"Page={pagina}" };
            if (status is not null)            query.Add($"Status={status}");
            if (integrationStatus is not null) query.Add($"IntegrationStatus={integrationStatus}");

            if (!string.IsNullOrWhiteSpace(desde))
            {
                query.Add($"Since={desde}");

                // TypeDateFilter=2 hace que Since se compare contra SendDate.
                // Por defecto la API compara contra CloseDate, y un informe que
                // todavia no se cierra tiene ClosedDate en null: sin esto, los
                // informes abiertos nunca aparecen aunque esten enviados.
                query.Add("TypeDateFilter=2");
            }

            var url = "getExpenseReports?" + string.Join("&", query);
            var resp = await EnviarAsync<RespuestaInformes>(HttpMethod.Get, url, null, "getExpenseReports", ct);

            if (resp?.ExpenseReports is null || resp.ExpenseReports.Count == 0) break;

            todos.AddRange(resp.ExpenseReports);
            _logger.LogInformation("getExpenseReports pagina {Pagina}/{Total}: {N} informes",
                pagina, resp.Records?.Pages ?? 1, resp.ExpenseReports.Count);

            if (resp.Records is null || pagina >= resp.Records.Pages) break;
            pagina++;
        }

        return todos;
    }

    /// <summary>
    /// Trae todos los gastos que pertenecen a un informe.
    /// getExpenses acepta ReportId como filtro; es la forma documentada de
    /// resolver que gastos entran en cada rendicion.
    /// </summary>
    public async Task<List<GastoApi>> ObtenerGastosPorInformeAsync(long reportId, CancellationToken ct)
    {
        var todos = new List<GastoApi>();
        var pagina = 1;

        while (true)
        {
            var url = $"getExpenses?ReportId={reportId}" +
                      $"&ResultsPerPage={_opciones.ResultadosPorPagina}&Page={pagina}";
            var resp = await EnviarAsync<RespuestaGastos>(HttpMethod.Get, url, null, "getExpenses(ReportId)", ct);

            if (resp?.Expenses is null || resp.Expenses.Count == 0) break;

            todos.AddRange(resp.Expenses);
            if (resp.Records is null || pagina >= resp.Records.Pages) break;
            pagina++;
        }

        return todos;
    }

    /// <summary>
    /// Marca informes como integrados. Rindegastos pide hacerlo ademas de marcar
    /// cada gasto: mientras el informe siga sin marcar, vuelve a aparecer en
    /// getExpenseReports como pendiente.
    /// </summary>
    public async Task MarcarInformesIntegradosAsync(
        IReadOnlyList<MarcaIntegracionInforme> marcas, CancellationToken ct)
    {
        foreach (var bloque in marcas.Chunk(100))
        {
            await EnviarAsync<JsonElement>(
                HttpMethod.Put, "setExpenseReportIntegrationBulk", new { ExpenseReports = bloque },
                "setExpenseReportIntegrationBulk", ct);

            _logger.LogInformation("setExpenseReportIntegrationBulk: {N} informes marcados", bloque.Length);
        }
    }

    // --------------------------------------------------------------- fondos

    /// <summary>
    /// Descarga los fondos (cajas chicas y entregas) recorriendo todas las paginas.
    /// Cada deposito de un fondo es una entrega de dinero que en el ERP se
    /// registra como un comprobante de transferencia.
    /// </summary>
    /// <param name="integrationStatus">0 = aun no integrados en el ERP. null = todos.</param>
    public async Task<List<FondoApi>> ObtenerFondosAsync(int? integrationStatus, CancellationToken ct)
    {
        var todos = new List<FondoApi>();
        var pagina = 1;

        while (true)
        {
            var query = new List<string> { $"ResultsPerPage={_opciones.ResultadosPorPagina}", $"Page={pagina}" };
            if (integrationStatus is not null) query.Add($"IntegrationStatus={integrationStatus}");

            var url = "getFunds?" + string.Join("&", query);
            var resp = await EnviarAsync<RespuestaFondos>(HttpMethod.Get, url, null, "getFunds", ct);

            if (resp?.Funds is null || resp.Funds.Count == 0) break;

            todos.AddRange(resp.Funds);
            _logger.LogInformation("getFunds pagina {Pagina}/{Total}: {N} fondos",
                pagina, resp.Records?.Pages ?? 1, resp.Funds.Count);

            if (resp.Records is null || pagina >= resp.Records.Pages) break;
            pagina++;
        }

        return todos;
    }

    /// <summary>
    /// Marca fondos como integrados. Solo se marca cuando TODOS sus depositos
    /// tienen comprobante: la marca es del fondo completo, no de cada deposito.
    /// </summary>
    public async Task MarcarFondosIntegradosAsync(
        IReadOnlyList<MarcaIntegracionFondo> marcas, CancellationToken ct)
    {
        foreach (var bloque in marcas.Chunk(100))
        {
            await EnviarAsync<JsonElement>(
                HttpMethod.Put, "setFundIntegrationBulk", new { Funds = bloque },
                "setFundIntegrationBulk", ct);

            _logger.LogInformation("setFundIntegrationBulk: {N} fondos marcados", bloque.Length);
        }
    }

    // ------------------------------------------- solicitudes de fondo

    /// <summary>
    /// Descarga las solicitudes de fondo (viaticos y entregas a rendir pedidos
    /// por adelantado) recorriendo todas las paginas.
    /// </summary>
    public async Task<List<SolicitudFondoApi>> ObtenerSolicitudesFondoAsync(CancellationToken ct)
    {
        var todas = new List<SolicitudFondoApi>();
        var pagina = 1;

        while (true)
        {
            var url = $"getFundsRequest?ResultsPerPage={_opciones.ResultadosPorPagina}&Page={pagina}";
            var resp = await EnviarAsync<RespuestaSolicitudesFondo>(HttpMethod.Get, url, null, "getFundsRequest", ct);

            if (resp?.FundsRequest is null || resp.FundsRequest.Count == 0) break;

            todas.AddRange(resp.FundsRequest);
            _logger.LogInformation("getFundsRequest pagina {Pagina}/{Total}: {N} solicitudes",
                pagina, resp.Records?.Pages ?? 1, resp.FundsRequest.Count);

            if (resp.Records is null || pagina >= resp.Records.Pages) break;
            pagina++;
        }

        return todas;
    }

    /// <summary>Marca solicitudes de fondo como integradas.</summary>
    public async Task MarcarSolicitudesFondoIntegradasAsync(
        IReadOnlyList<MarcaIntegracionSolicitud> marcas, CancellationToken ct)
    {
        foreach (var bloque in marcas.Chunk(100))
        {
            await EnviarAsync<JsonElement>(
                HttpMethod.Put, "setFundRequestIntegrationBulk", new { FundsRequest = bloque },
                "setFundRequestIntegrationBulk", ct);

            _logger.LogInformation("setFundRequestIntegrationBulk: {N} solicitudes marcadas", bloque.Length);
        }
    }

    /// <summary>
    /// Envia la peticion, deja constancia en rg_log_api y deserializa la respuesta.
    /// Los errores 4xx no se reintentan (el problema es el payload, no la red).
    /// </summary>
    private async Task<T?> EnviarAsync<T>(
        HttpMethod metodo, string url, object? cuerpo, string nombreMetodo, CancellationToken ct)
    {
        var cronometro = Stopwatch.StartNew();
        var request = new HttpRequestMessage(metodo, url);
        string? jsonRequest = null;

        if (cuerpo is not null)
        {
            jsonRequest = JsonSerializer.Serialize(cuerpo);
            request.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await _http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            cronometro.Stop();
            await _log.RegistrarAsync(nombreMetodo, null, (int)cronometro.ElapsedMilliseconds,
                                      false, jsonRequest ?? url, ex.Message, ct);
            throw;
        }

        cronometro.Stop();
        var texto = await respuesta.Content.ReadAsStringAsync(ct);

        // Rindegastos a veces responde HTTP 200 con un error adentro:
        //   {"message":[...],"error":"Bad Request","statusCode":400}
        // Si solo se mira el codigo HTTP, eso pasa por exito y el gasto queda
        // como confirmado sin que la marca haya llegado. Asi estuvo hasta el
        // 17/09/2026: ninguna marca de integracion se habia aplicado.
        var errorEnCuerpo = ErrorEnCuerpo(texto);
        var ok = respuesta.IsSuccessStatusCode && errorEnCuerpo is null;

        await _log.RegistrarAsync(nombreMetodo, errorEnCuerpo?.Codigo ?? (int)respuesta.StatusCode,
                                  (int)cronometro.ElapsedMilliseconds, ok, jsonRequest ?? url, Recortar(texto), ct);

        if (!respuesta.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{nombreMetodo} devolvio {(int)respuesta.StatusCode}: {Recortar(texto, 500)}",
                null, respuesta.StatusCode);

        if (errorEnCuerpo is not null)
            throw new HttpRequestException(
                $"{nombreMetodo} respondio HTTP {(int)respuesta.StatusCode} pero con error " +
                $"{errorEnCuerpo.Value.Codigo} en el cuerpo: {Recortar(texto, 500)}");

        return string.IsNullOrWhiteSpace(texto) ? default : JsonSerializer.Deserialize<T>(texto, JsonOpts);
    }

    /// <summary>
    /// Detecta la respuesta de error que Rindegastos manda con HTTP 200:
    /// un objeto con "statusCode" de 400 o mas. Devuelve null si no es un error.
    /// </summary>
    private static (int Codigo, string Texto)? ErrorEnCuerpo(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto) || texto.TrimStart()[0] != '{') return null;

        try
        {
            using var doc = JsonDocument.Parse(texto);
            var raiz = doc.RootElement;

            if (raiz.TryGetProperty("statusCode", out var sc)
                && sc.ValueKind == JsonValueKind.Number
                && sc.TryGetInt32(out var codigo)
                && codigo >= 400)
                return (codigo, texto);
        }
        catch (JsonException)
        {
            // No es JSON: se deja pasar y lo decide el codigo HTTP.
        }

        return null;
    }

    private static string Recortar(string texto, int max = 100_000)
        => texto.Length <= max ? texto : texto[..max] + " ...[recortado]";
}
