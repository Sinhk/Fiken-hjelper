using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FikenHjelper.Core.Config;

namespace FikenHjelper.Core.Fiken;

public sealed class FikenOAuthService : IFikenOAuthService, IDisposable
{
    public const string HttpClientName = "FikenOAuth";
    public const string AuthorizeEndpoint = "https://fiken.no/oauth/authorize";
    public const string TokenEndpoint = "https://fiken.no/oauth/token";
    public const string RevokeEndpoint = "https://fiken.no/oauth/revoke";

    private static readonly TimeSpan ExpiryRefreshMargin = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<HttpClient> _httpClientFactory;
    private readonly ILocalConfigStore _configStore;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly bool _disposeCreatedClients;

    public FikenOAuthService(HttpClient httpClient, ILocalConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClientFactory = () => httpClient;
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _disposeCreatedClients = false;
    }

    public FikenOAuthService(Func<HttpClient> httpClientFactory, ILocalConfigStore configStore)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _disposeCreatedClients = true;
    }

    public string BuildAuthorizationUrl(
        string clientId,
        string redirectUri,
        string state,
        string codeChallenge)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID må fylles ut.", nameof(clientId));
        }

        return $"{AuthorizeEndpoint}?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId.Trim())}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}"
            + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
            + "&code_challenge_method=S256";
    }

    public async Task<FikenOAuthTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        string state,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new FikenApiException(HttpStatusCode.BadRequest, "Mangler authorization code fra Fiken.");
        }

        var settings = await _configStore.LoadSettingsAsync(ct);
        var clientId = settings.FikenOAuthClientId.Trim();
        var clientSecret = settings.FikenOAuthClientSecret.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new FikenApiException(
                HttpStatusCode.BadRequest,
                "Fiken OAuth2 Client ID mangler. Legg den inn under Innstillinger.");
        }

        var formFields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code.Trim(),
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_verifier"] = codeVerifier
        };

        var isConfidential = !string.IsNullOrWhiteSpace(clientSecret);
        if (!isConfidential)
        {
            formFields["client_id"] = clientId;
        }

        var tokenResponse = await PostTokenFormAsync(formFields, isConfidential ? (clientId, clientSecret) : null, isRefresh: false, ct);
        var expiresAtUtc = ComputeExpiresAtUtc(tokenResponse.ExpiresIn);

        await _configStore.SaveFikenOAuthTokensAsync(
            tokenResponse.AccessToken,
            tokenResponse.RefreshToken,
            expiresAtUtc,
            ct);

        return tokenResponse;
    }

    public async Task<string> GetValidAccessTokenAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var settings = await _configStore.LoadSettingsAsync(ct);
        if (!forceRefresh && HasFreshAccessToken(settings))
        {
            return settings.FikenOAuthAccessToken;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            settings = await _configStore.LoadSettingsAsync(ct);
            if (!forceRefresh && HasFreshAccessToken(settings))
            {
                return settings.FikenOAuthAccessToken;
            }

            if (string.IsNullOrWhiteSpace(settings.FikenOAuthRefreshToken))
            {
                if (!forceRefresh && !string.IsNullOrWhiteSpace(settings.FikenOAuthAccessToken))
                {
                    return settings.FikenOAuthAccessToken;
                }

                throw new FikenApiException(
                    HttpStatusCode.Unauthorized,
                    "Fiken OAuth2-tilkoblingen mangler refresh token. Logg inn på nytt med Fiken under Innstillinger.");
            }

            var clientId = settings.FikenOAuthClientId.Trim();
            var clientSecret = settings.FikenOAuthClientSecret.Trim();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new FikenApiException(
                    HttpStatusCode.Unauthorized,
                    "Fiken OAuth2 Client ID mangler under Innstillinger.");
            }

            var formFields = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = settings.FikenOAuthRefreshToken.Trim()
            };

            var isConfidential = !string.IsNullOrWhiteSpace(clientSecret);
            if (!isConfidential)
            {
                formFields["client_id"] = clientId;
            }

            var refreshed = await PostTokenFormAsync(
                formFields,
                isConfidential ? (clientId, clientSecret) : null,
                isRefresh: true,
                ct);

            var expiresAtUtc = ComputeExpiresAtUtc(refreshed.ExpiresIn);
            await _configStore.SaveFikenOAuthTokensAsync(
                refreshed.AccessToken,
                refreshed.RefreshToken,
                expiresAtUtc,
                ct);

            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var settings = await _configStore.LoadSettingsAsync(ct);
        if (!string.IsNullOrWhiteSpace(settings.FikenOAuthAccessToken))
        {
            HttpClient? client = null;
            try
            {
                client = _httpClientFactory();
                using var req = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.FikenOAuthAccessToken.Trim());
                using var revokeResponse = await client.SendAsync(req, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best-effort revoke; always clear local tokens below.
            }
            finally
            {
                if (_disposeCreatedClients)
                {
                    client?.Dispose();
                }
            }
        }

        await _configStore.ClearFikenOAuthTokensAsync(ct);
    }

    private async Task<FikenOAuthTokenResponse> PostTokenFormAsync(
        Dictionary<string, string> formFields,
        (string ClientId, string ClientSecret)? basicCredentials,
        bool isRefresh,
        CancellationToken ct)
    {
        var client = _httpClientFactory();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(formFields)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (basicCredentials is { } creds)
            {
                var raw = $"{creds.ClientId}:{creds.ClientSecret}";
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                FikenOAuthErrorResponse? errorBody = null;
                try
                {
                    errorBody = await response.Content.ReadFromJsonAsync<FikenOAuthErrorResponse>(JsonOptions, ct);
                }
                catch
                {
                    // Ignore malformed error body.
                }

                if (isRefresh && (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized))
                {
                    await _configStore.ClearFikenOAuthTokensAsync(ct);
                    throw new FikenApiException(
                        HttpStatusCode.Unauthorized,
                        "Fiken-innloggingen er utløpt eller trukket tilbake. Logg inn på nytt med Fiken under Innstillinger.",
                        errorBody?.ErrorDescription ?? errorBody?.Error);
                }

                var detail = errorBody?.ErrorDescription ?? errorBody?.Error;
                throw new FikenApiException(
                    response.StatusCode,
                    "Fiken OAuth2-autentisering feilet. Kontroller Client ID, Client Secret og Redirect URI.",
                    detail);
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<FikenOAuthTokenResponse>(JsonOptions, ct);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new FikenApiException(
                    HttpStatusCode.BadGateway,
                    "Fiken returnerte et tomt OAuth2-token-svar.");
            }

            return tokenResponse;
        }
        finally
        {
            if (_disposeCreatedClients)
            {
                client.Dispose();
            }
        }
    }

    private static bool HasFreshAccessToken(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FikenOAuthAccessToken))
        {
            return false;
        }

        return settings.FikenOAuthExpiresAtUtc is null
            || settings.FikenOAuthExpiresAtUtc.Value > DateTimeOffset.UtcNow.Add(ExpiryRefreshMargin);
    }

    private static DateTimeOffset? ComputeExpiresAtUtc(int expiresInSeconds)
        => expiresInSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds) : null;

    public void Dispose() => _refreshGate.Dispose();
}
