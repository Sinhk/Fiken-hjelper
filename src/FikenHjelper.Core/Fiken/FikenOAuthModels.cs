using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Fiken;

public sealed record FikenOAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record FikenOAuthErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

public sealed record PendingOAuthRequest(
    string CodeVerifier,
    string RedirectUri,
    DateTimeOffset CreatedAtUtc);

public static class FikenPkceHelper
{
    public static string CreateRandomBase64Url(int numBytes = 48)
    {
        var bytes = RandomNumberGenerator.GetBytes(numBytes);
        return ToBase64Url(bytes);
    }

    public static string ComputeS256Challenge(string codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(codeVerifier))
        {
            throw new ArgumentException("Code verifier kan ikke være tom.", nameof(codeVerifier));
        }

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return ToBase64Url(hash);
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
