using System.Collections.Concurrent;
using System.Security.Cryptography;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Mcp.Safety;

public sealed record PendingDraft(
    string CompanySlug,
    FikenPurchaseDraftRequest Request,
    PurchaseDocumentSource? Source,
    string Summary,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Holder forhåndsviste kjøpsutkast i minnet til brukeren har bekreftet. Opprettelse skjer bare med tokenet
/// fra forhåndsvisningen, slik at det som lagres i Fiken er nøyaktig det brukeren så. Tokenet kan brukes én gang.
/// </summary>
public sealed class PendingDraftStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, PendingDraft> _pending = new(StringComparer.Ordinal);

    public (string Token, DateTimeOffset ExpiresAt) Add(
        string companySlug,
        FikenPurchaseDraftRequest request,
        PurchaseDocumentSource? source,
        string summary)
    {
        RemoveExpired();
        var token = Base64UrlToken(RandomNumberGenerator.GetBytes(16));
        var expiresAt = time.GetUtcNow() + Lifetime;
        _pending[token] = new PendingDraft(companySlug, request, source, summary, expiresAt);
        return (token, expiresAt);
    }

    /// <summary>Henter og fjerner et utkast. Returnerer false hvis tokenet er ukjent, brukt eller utløpt.</summary>
    public bool TryTake(string? token, out PendingDraft? draft)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(token) || !_pending.TryRemove(token.Trim(), out var found))
        {
            return false;
        }

        if (found.ExpiresAt <= time.GetUtcNow())
        {
            return false;
        }

        draft = found;
        return true;
    }

    private void RemoveExpired()
    {
        var now = time.GetUtcNow();
        foreach (var (key, value) in _pending)
        {
            if (value.ExpiresAt <= now)
            {
                _pending.TryRemove(key, out _);
            }
        }
    }

    private static string Base64UrlToken(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
