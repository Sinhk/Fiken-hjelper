using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FikenHjelper.Core.Config;

namespace FikenHjelper.Core.Fiken;

public sealed partial class FikenClient : IFikenClient
{
    public const string DefaultBaseAddress = "https://api.fiken.no/api/v2/";
    private const int MaxPageSize = 100;
    private const int SafetyMaxPages = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILocalConfigStore _configStore;
    private readonly IFikenOAuthService? _oauthService;

    private readonly record struct AuthResolution(string Token, bool UsedOAuth);

    public FikenClient(
        HttpClient http,
        ILocalConfigStore configStore,
        IFikenOAuthService? oauthService = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _oauthService = oauthService;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(DefaultBaseAddress);
        }
    }

    public async Task<IReadOnlyList<FikenCompany>> GetCompaniesAsync(
        string? apiTokenOverride = null,
        CancellationToken ct = default)
    {
        var auth = await ResolveAuthAsync(apiTokenOverride, ct);
        return await GetAllPagesAsync<FikenCompany>("companies", auth, ct);
    }

    public async Task<IReadOnlyList<FikenContact>> GetCustomersAsync(
        string companySlug,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/contacts?customer=true&inactive=false";
        return await GetAllPagesAsync<FikenContact>(path, auth, ct);
    }

    public async Task<CreatedResourceResult> CreateCustomerAsync(
        string companySlug,
        FikenCreateContactRequest request,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        ArgumentNullException.ThrowIfNull(request);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/contacts";
        return await PostForLocationAsync(path, request, auth, ct);
    }

    public async Task<IReadOnlyList<FikenProject>> GetProjectsAsync(
        string companySlug,
        bool? completed = null,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/projects";
        if (completed.HasValue)
        {
            path += $"?completed={(completed.Value ? "true" : "false")}";
        }

        return await GetAllPagesAsync<FikenProject>(path, auth, ct);
    }

    public async Task<IReadOnlyList<FikenAccount>> GetIncomeAccountsAsync(
        string companySlug,
        CancellationToken ct = default,
        string? apiTokenOverride = null)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(apiTokenOverride, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/accounts?range=3000-3999";
        return await GetAllPagesAsync<FikenAccount>(path, auth, ct);
    }

    public async Task<IReadOnlyList<FikenBankAccount>> GetBankAccountsAsync(
        string companySlug,
        CancellationToken ct = default,
        string? apiTokenOverride = null)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(apiTokenOverride, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/bankAccounts?inactive=false";
        return await GetAllPagesAsync<FikenBankAccount>(path, auth, ct);
    }

    public async Task<IReadOnlyList<FikenInvoiceDraftSummary>> GetInvoiceDraftsAsync(
        string companySlug,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/invoices/drafts";
        return await GetAllPagesAsync<FikenInvoiceDraftSummary>(path, auth, ct);
    }

    public async Task<CreatedResourceResult> CreateInvoiceDraftAsync(
        string companySlug,
        FikenInvoiceDraftRequest request,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        ArgumentNullException.ThrowIfNull(request);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/invoices/drafts";
        return await PostForLocationAsync(path, request, auth, ct);
    }

    public async Task<IReadOnlyList<FikenJournalEntry>> GetJournalEntriesAsync(
        string companySlug,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/journalEntries"
                   + $"?dateGe={from:yyyy-MM-dd}&dateLe={to:yyyy-MM-dd}";
        return await GetAllPagesAsync<FikenJournalEntry>(path, auth, ct);
    }

    public async Task<IReadOnlyList<FikenInvoice>> GetUnsettledInvoicesAsync(
        string companySlug,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"companies/{Uri.EscapeDataString(companySlug)}/invoices?settled=false";
        return await GetAllPagesAsync<FikenInvoice>(path, auth, ct);
    }

    private async Task<AuthResolution> ResolveAuthAsync(string? overrideToken, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(overrideToken))
        {
            return new AuthResolution(overrideToken.Trim(), UsedOAuth: false);
        }

        var settings = await _configStore.LoadSettingsAsync(ct);

        if (settings.HasFikenOAuthTokens && _oauthService is not null)
        {
            var accessToken = await _oauthService.GetValidAccessTokenAsync(forceRefresh: false, ct);
            return new AuthResolution(accessToken, UsedOAuth: true);
        }

        if (!string.IsNullOrWhiteSpace(settings.FikenApiToken))
        {
            return new AuthResolution(settings.FikenApiToken.Trim(), UsedOAuth: false);
        }

        throw new FikenApiException(
            HttpStatusCode.Unauthorized,
            "Ikke koblet til Fiken. Logg inn med Fiken OAuth2 eller legg inn personlig API-nøkkel under Innstillinger.");
    }

    private async Task<IReadOnlyList<T>> GetAllPagesAsync<T>(
        string basePath,
        AuthResolution auth,
        CancellationToken ct)
    {
        var results = new List<T>();
        var separator = basePath.Contains('?') ? '&' : '?';

        for (var page = 0; page < SafetyMaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{basePath}{separator}page={page}&pageSize={MaxPageSize}";

            var (response, updatedAuth) = await SendWithOAuthRetryAsync(
                token =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    return req;
                },
                auth,
                ct);

            auth = updatedAuth;
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw await BuildApiExceptionAsync(response, ct);
                }

                var pageItems = await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct) ?? [];
                results.AddRange(pageItems);

                if (TryGetHeaderInt(response, "Fiken-Api-Page-Count", out var pageCount))
                {
                    if (page + 1 >= pageCount)
                    {
                        break;
                    }
                }
                else if (pageItems.Count < MaxPageSize)
                {
                    break;
                }
            }
        }

        return results;
    }

    private async Task<CreatedResourceResult> PostForLocationAsync<TBody>(
        string path,
        TBody body,
        AuthResolution auth,
        CancellationToken ct)
    {
        var (response, _) = await SendWithOAuthRetryAsync(
            token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = JsonContent.Create(body, options: JsonOptions)
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return req;
            },
            auth,
            ct);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildApiExceptionAsync(response, ct);
            }

            var location = response.Headers.Location;
            var id = ExtractTrailingId(location);
            return new CreatedResourceResult(id, location);
        }
    }

    private async Task<(HttpResponseMessage Response, AuthResolution Auth)> SendWithOAuthRetryAsync(
        Func<string, HttpRequestMessage> requestFactory,
        AuthResolution auth,
        CancellationToken ct)
    {
        using var initialRequest = requestFactory(auth.Token);
        var response = await _http.SendAsync(initialRequest, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && auth.UsedOAuth && _oauthService is not null)
        {
            response.Dispose();
            var freshToken = await _oauthService.GetValidAccessTokenAsync(forceRefresh: true, ct, rejectedAccessToken: auth.Token);
            var refreshedAuth = auth with { Token = freshToken };

            using var retryRequest = requestFactory(refreshedAuth.Token);
            response = await _http.SendAsync(retryRequest, ct);
            return (response, refreshedAuth);
        }

        return (response, auth);
    }

    private static long? ExtractTrailingId(Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        var raw = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var lastSegment = raw.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return long.TryParse(lastSegment, out var id) ? id : null;
    }

    private static bool TryGetHeaderInt(HttpResponseMessage response, string headerName, out int value)
    {
        value = 0;
        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        return int.TryParse(values.FirstOrDefault(), out value);
    }

    private static async Task<FikenApiException> BuildApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        string? rawBody = null;
        try
        {
            rawBody = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            // Ignore body read failures when building error details.
        }

        var sanitizedDetail = SanitizeErrorDetail(rawBody);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Ugyldig Fiken API-nøkkel. Sjekk at nøkkelen er limt inn riktig under Innstillinger.",
            HttpStatusCode.Forbidden => "Tilgang nektet i Fiken. Sjekk at API-modulen er aktivert for dette foretaket.",
            HttpStatusCode.NotFound => "Fant ikke forespurt ressurs eller foretak i Fiken.",
            HttpStatusCode.TooManyRequests => "For mange kall mot Fiken API på kort tid. Vent et øyeblikk og prøv igjen.",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                "Fiken avviste forespørselen på grunn av ugyldige eller manglende felter.",
            _ => $"Uventet svar fra Fiken API (HTTP {(int)response.StatusCode})."
        };

        return new FikenApiException(response.StatusCode, message, sanitizedDetail);
    }

    private static string? SanitizeErrorDetail(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        var trimmed = rawBody.Trim();
        if (trimmed.StartsWith('<'))
        {
            return null;
        }

        return trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed;
    }

    private static void ValidateSlug(string companySlug)
    {
        if (string.IsNullOrWhiteSpace(companySlug))
        {
            throw new FikenApiException(
                HttpStatusCode.BadRequest,
                "Ingen Fiken-foretak valgt. Velg foretak først.");
        }
    }
}
