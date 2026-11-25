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

    /// <param name="rejectedAccessToken">
    /// Tokenet Fiken nettopp avviste (ved <paramref name="forceRefresh"/>). Har en annen prosess allerede fornyet,
    /// brukes det nye tokenet uten ny fornyelse.
    /// </param>
    Task<string> GetValidAccessTokenAsync(
        bool forceRefresh = false,
        CancellationToken ct = default,
        string? rejectedAccessToken = null);

    Task DisconnectAsync(CancellationToken ct = default);
}
