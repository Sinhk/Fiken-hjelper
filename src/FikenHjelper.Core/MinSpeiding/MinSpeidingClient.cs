using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FikenHjelper.Core.Config;

namespace FikenHjelper.Core.MinSpeiding;

public sealed class MinSpeidingApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string UserMessage { get; }

    public MinSpeidingApiException(HttpStatusCode statusCode, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        StatusCode = statusCode;
        UserMessage = userMessage;
    }
}

public sealed class MinSpeidingClient : IMinSpeidingClient
{
    private readonly HttpClient _http;
    private readonly ILocalConfigStore _configStore;

    public MinSpeidingClient(HttpClient http, ILocalConfigStore configStore)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
    }

    public static string CreateBasicAuthParameter(string projectId, string apiKey)
    {
        var credentials = $"{projectId.Trim()}:{apiKey.Trim()}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
    }

    public async Task<MinSpeidingEventLoadResult> GetEventParticipantsAsync(
        string projectId,
        string apiKey,
        string? baseUrlOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new MinSpeidingApiException(HttpStatusCode.BadRequest, "Prosjekt-ID fra Min speiding må fylles ut.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new MinSpeidingApiException(HttpStatusCode.Unauthorized, "API-nøkkel for arrangementet må fylles ut.");
        }

        var settings = await _configStore.LoadSettingsAsync(ct);
        var baseUrl = string.IsNullOrWhiteSpace(baseUrlOverride)
            ? settings.MinSpeidingBaseUrl
            : baseUrlOverride;
        baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? "https://min.speiding.no/api" : baseUrl).Trim().TrimEnd('/');

        var authParam = CreateBasicAuthParameter(projectId, apiKey);

        var participantsJson = await FetchJsonAsync($"{baseUrl}/project/get/participants", authParam, ct);

        string? questionsJson = null;
        var questionsAvailable = false;
        try
        {
            using var qReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/project/get/questions");
            qReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authParam);
            qReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var qRes = await _http.SendAsync(qReq, ct);
            if (qRes.IsSuccessStatusCode)
            {
                questionsJson = await qRes.Content.ReadAsStringAsync(ct);
                questionsAvailable = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort only: continue cleanly if questions endpoint is unavailable.
        }

        try
        {
            return MinSpeidingParser.Parse(projectId.Trim(), participantsJson, questionsJson, questionsAvailable);
        }
        catch (FormatException ex)
        {
            throw new MinSpeidingApiException(HttpStatusCode.BadGateway, ex.Message, ex);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new MinSpeidingApiException(
                HttpStatusCode.BadGateway,
                "Svaret fra Min speiding hadde uventet JSON-format.",
                ex);
        }
    }

    private async Task<string> FetchJsonAsync(string url, string authParam, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authParam);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Min speiding avviste prosjekt-ID eller API-nøkkel (401/403). Sjekk nøkkelen under Prosjekt -> Webtjenester.",
                HttpStatusCode.NotFound =>
                    "Fant ikke Min speiding-endepunktet (404). Sjekk Base-URL under Innstillinger.",
                _ => $"Feil fra Min speiding API (HTTP {(int)response.StatusCode})."
            };
            throw new MinSpeidingApiException(response.StatusCode, message);
        }

        return await response.Content.ReadAsStringAsync(ct);
    }
}
