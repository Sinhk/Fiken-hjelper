using FikenHjelper.Core.Config;

namespace FikenHjelper.Mcp.Services;

/// <summary>
/// Lar MCP-serveren bruke et annet foretak (f.eks. testforetak) enn Web, uten å endre config.json.
/// Tokens leses og skrives som vanlig, så innloggingen deles med Web.
/// </summary>
public sealed class CompanySlugOverrideConfigStore(ILocalConfigStore inner, string companySlug) : ILocalConfigStore
{
    public string DataDirectory => inner.DataDirectory;

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        var settings = await inner.LoadSettingsAsync(ct);
        settings.FikenCompanySlug = companySlug;
        return settings;
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) =>
        throw new InvalidOperationException("MCP-serveren endrer ikke innstillingene. Bruk Fiken-hjelper Web.");

    public Task SaveFikenOAuthTokensAsync(string accessToken, string? refreshToken, DateTimeOffset? expiresAtUtc, CancellationToken ct = default) =>
        inner.SaveFikenOAuthTokensAsync(accessToken, refreshToken, expiresAtUtc, ct);

    public Task ClearFikenOAuthTokensAsync(CancellationToken ct = default) => inner.ClearFikenOAuthTokensAsync(ct);

    public Task<MappingsData> LoadMappingsAsync(CancellationToken ct = default) => inner.LoadMappingsAsync(ct);

    public Task SaveMappingsAsync(MappingsData mappings, CancellationToken ct = default) => inner.SaveMappingsAsync(mappings, ct);

    public Task SaveGroupMappingAsync(string groupName, long contactId, CancellationToken ct = default) =>
        inner.SaveGroupMappingAsync(groupName, contactId, ct);

    public Task RemoveGroupMappingAsync(string groupName, CancellationToken ct = default) => inner.RemoveGroupMappingAsync(groupName, ct);

    public Task SaveEventAsync(SavedMinSpeidingEvent savedEvent, CancellationToken ct = default) => inner.SaveEventAsync(savedEvent, ct);

    public Task RemoveEventAsync(string projectId, CancellationToken ct = default) => inner.RemoveEventAsync(projectId, ct);
}
