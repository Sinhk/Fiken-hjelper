namespace FikenHjelper.Core.Fiken;

public interface IFikenOAuthService
{
    string BuildAuthorizationUrl(
        string clientId,
        string redirectUri,
        string state,
        string codeChallenge);

    Task<FikenOAuthTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        string state,
        CancellationToken ct = default);

    Task<string> GetValidAccessTokenAsync(
        bool forceRefresh = false,
        CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);
}
