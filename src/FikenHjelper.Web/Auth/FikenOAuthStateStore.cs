using System.Collections.Concurrent;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Web.Auth;

public sealed class FikenOAuthStateStore
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PendingOAuthRequest> _pending = new(StringComparer.Ordinal);

    public string CreateState(string codeVerifier, string redirectUri)
    {
        CleanupExpired();
        var state = FikenPkceHelper.CreateRandomBase64Url(32);
        _pending[state] = new PendingOAuthRequest(codeVerifier, redirectUri, DateTimeOffset.UtcNow);
        return state;
    }

    public bool TryConsume(string state, out PendingOAuthRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        if (!_pending.TryRemove(state, out var found))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - found.CreatedAtUtc > StateLifetime)
        {
            return false;
        }

        request = found;
        return true;
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - StateLifetime;
        foreach (var (key, value) in _pending)
        {
            if (value.CreatedAtUtc < cutoff)
            {
                _pending.TryRemove(key, out _);
            }
        }
    }
}
