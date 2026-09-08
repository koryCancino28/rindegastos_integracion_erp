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
    /// </summary>
    public async Task MarcarGastosIntegradosAsync(IReadOnlyList<MarcaIntegracion> marcas, CancellationToken ct)
    {
        foreach (var bloque in marcas.Chunk(100))
        {
            await EnviarAsync<JsonElement>(
                HttpMethod.Put, "setExpenseIntegrationBulk", bloque, "setExpenseIntegrationBulk", ct);

            _logger.LogInformation("setExpenseIntegrationBulk: {N} gastos marcados", bloque.Length);
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

        await _log.RegistrarAsync(nombreMetodo, (int)respuesta.StatusCode, (int)cronometro.ElapsedMilliseconds,
                                  respuesta.IsSuccessStatusCode, jsonRequest ?? url, Recortar(texto), ct);

        if (!respuesta.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{nombreMetodo} devolvio {(int)respuesta.StatusCode}: {Recortar(texto, 500)}",
                null, respuesta.StatusCode);

        return string.IsNullOrWhiteSpace(texto) ? default : JsonSerializer.Deserialize<T>(texto, JsonOpts);
    }

    private static string Recortar(string texto, int max = 100_000)
        => texto.Length <= max ? texto : texto[..max] + " ...[recortado]";
}
