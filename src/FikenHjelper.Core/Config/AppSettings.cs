using System.Text.Json;
using System.Text.Json.Serialization;
using FikenHjelper.Core.Invoicing;

namespace FikenHjelper.Core.Config;

public sealed class AppSettings
{
    public string FikenApiToken { get; set; } = string.Empty;
    public string FikenOAuthClientId { get; set; } = string.Empty;
    public string FikenOAuthClientSecret { get; set; } = string.Empty;
    public string FikenOAuthAccessToken { get; set; } = string.Empty;
    public string FikenOAuthRefreshToken { get; set; } = string.Empty;
    public DateTimeOffset? FikenOAuthExpiresAtUtc { get; set; }
    public string FikenCompanySlug { get; set; } = string.Empty;
    public string MinSpeidingBaseUrl { get; set; } = "https://min.speiding.no/api";
    public string DefaultIncomeAccount { get; set; } = "3200";
    public int DefaultDaysUntilDueDate { get; set; } = 14;
    public string DefaultBankAccountNumber { get; set; } = string.Empty;
    public string DefaultOurReference { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasFikenOAuthTokens =>
        !string.IsNullOrWhiteSpace(FikenOAuthAccessToken)
        || !string.IsNullOrWhiteSpace(FikenOAuthRefreshToken);

    [JsonIgnore]
    public bool IsFikenConfigured =>
        HasFikenOAuthTokens
        || !string.IsNullOrWhiteSpace(FikenApiToken);

    public AppSettings Clone() => new()
    {
        FikenApiToken = FikenApiToken,
        FikenOAuthClientId = FikenOAuthClientId,
        FikenOAuthClientSecret = FikenOAuthClientSecret,
        FikenOAuthAccessToken = FikenOAuthAccessToken,
        FikenOAuthRefreshToken = FikenOAuthRefreshToken,
        FikenOAuthExpiresAtUtc = FikenOAuthExpiresAtUtc,
        FikenCompanySlug = FikenCompanySlug,
        MinSpeidingBaseUrl = MinSpeidingBaseUrl,
        DefaultIncomeAccount = DefaultIncomeAccount,
        DefaultDaysUntilDueDate = DefaultDaysUntilDueDate,
        DefaultBankAccountNumber = DefaultBankAccountNumber,
        DefaultOurReference = DefaultOurReference
    };
}

public sealed record AppSettingsDefaults(
    string? FikenApiToken = null,
    string? FikenCompanySlug = null,
    string? MinSpeidingBaseUrl = null,
    string? FikenOAuthClientId = null,
    string? FikenOAuthClientSecret = null);

public sealed record SavedMinSpeidingEvent(
    string Name,
    string ProjectId,
    string? ApiKey = null,
    string? QuestionsApiKey = null);

public sealed class MappingsData
{
    public Dictionary<string, long> GroupNameToContactId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SavedMinSpeidingEvent> SavedEvents { get; set; } = [];
}

public sealed class SavedEventDraftState
{
    public string ProjectId { get; set; } = string.Empty;
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long? FikenProjectId { get; set; }
    public string? IncomeAccount { get; set; }
    public string? BankAccountNumber { get; set; }
    public DateOnly? IssueDate { get; set; }
    public int? DaysUntilDueDate { get; set; }
    public string? InvoiceText { get; set; }
    public string? OurReference { get; set; }
    public string? OrderReferencePrefix { get; set; }
    public BillingMode BillingMode { get; set; } = BillingMode.PerScoutGroup;
    public GroupLineMode GroupLineMode { get; set; } = GroupLineMode.OneLinePerParticipant;
    public ParticipantFilterOptions Filters { get; set; } = new();
    public Dictionary<string, decimal> CategoryPricesNok { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CategoryLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExcludedCategoryKeys { get; set; } = [];
    public Dictionary<string, ParticipantOverride> ParticipantOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> ManualRecipientContactIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface ILocalConfigStore
{
    string DataDirectory { get; }
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    Task SaveFikenOAuthTokensAsync(
        string accessToken,
        string? refreshToken,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct = default);
    Task ClearFikenOAuthTokensAsync(CancellationToken ct = default);
    Task<MappingsData> LoadMappingsAsync(CancellationToken ct = default);
    Task SaveMappingsAsync(MappingsData mappings, CancellationToken ct = default);
    Task SaveGroupMappingAsync(string groupName, long contactId, CancellationToken ct = default);
    Task RemoveGroupMappingAsync(string groupName, CancellationToken ct = default);
    Task SaveEventAsync(SavedMinSpeidingEvent savedEvent, CancellationToken ct = default);
    Task RemoveEventAsync(string projectId, CancellationToken ct = default);
    Task<SavedEventDraftState?> LoadEventDraftStateAsync(string projectId, CancellationToken ct = default);
    Task SaveEventDraftStateAsync(SavedEventDraftState draftState, CancellationToken ct = default);
    Task DeleteEventDraftStateAsync(string projectId, CancellationToken ct = default);
}

public sealed class LocalConfigStore : ILocalConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppSettingsDefaults _defaults;
    private readonly string _configPath;
    private readonly string _mappingsPath;

    public string DataDirectory { get; }

    public LocalConfigStore(AppSettingsDefaults? defaults = null, string? dataDirectory = null)
    {
        _defaults = defaults ?? new AppSettingsDefaults();
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FikenHjelper")
            : dataDirectory;
        _configPath = Path.Combine(DataDirectory, "config.json");
        _mappingsPath = Path.Combine(DataDirectory, "mappings.json");
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await ReadJsonAsync<AppSettings>(_configPath, ct) ?? new AppSettings();
            ApplyDefaults(settings);
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(ct);
        try
        {
            var existing = await ReadJsonAsync<AppSettings>(_configPath, ct);

            var copy = settings.Clone();
            copy.FikenApiToken = copy.FikenApiToken.Trim();
            copy.FikenOAuthClientId = copy.FikenOAuthClientId.Trim();
            copy.FikenOAuthClientSecret = copy.FikenOAuthClientSecret.Trim();
            // Always preserve persisted OAuth tokens so saving settings never clobbers a freshly rotated refresh token.
            copy.FikenOAuthAccessToken = existing?.FikenOAuthAccessToken ?? string.Empty;
            copy.FikenOAuthRefreshToken = existing?.FikenOAuthRefreshToken ?? string.Empty;
            copy.FikenOAuthExpiresAtUtc = existing?.FikenOAuthExpiresAtUtc;
            copy.FikenCompanySlug = copy.FikenCompanySlug.Trim();
            copy.MinSpeidingBaseUrl = string.IsNullOrWhiteSpace(copy.MinSpeidingBaseUrl)
                ? "https://min.speiding.no/api"
                : copy.MinSpeidingBaseUrl.Trim().TrimEnd('/');
            copy.DefaultIncomeAccount = string.IsNullOrWhiteSpace(copy.DefaultIncomeAccount)
                ? "3200"
                : copy.DefaultIncomeAccount.Trim();
            copy.DefaultDaysUntilDueDate = Math.Clamp(copy.DefaultDaysUntilDueDate, 0, 365);
            copy.DefaultBankAccountNumber = copy.DefaultBankAccountNumber.Trim();
            copy.DefaultOurReference = copy.DefaultOurReference.Trim();

            await WriteJsonAsync(_configPath, copy, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveFikenOAuthTokensAsync(
        string accessToken,
        string? refreshToken,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token kan ikke være tomt.", nameof(accessToken));
        }

        await _gate.WaitAsync(ct);
        try
        {
            var settings = await ReadJsonAsync<AppSettings>(_configPath, ct) ?? new AppSettings();
            ApplyDefaults(settings);

            settings.FikenOAuthAccessToken = accessToken.Trim();
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                settings.FikenOAuthRefreshToken = refreshToken.Trim();
            }

            settings.FikenOAuthExpiresAtUtc = expiresAtUtc;
            await WriteJsonAsync(_configPath, settings, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearFikenOAuthTokensAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await ReadJsonAsync<AppSettings>(_configPath, ct);
            if (settings is null)
            {
                return;
            }

            settings.FikenOAuthAccessToken = string.Empty;
            settings.FikenOAuthRefreshToken = string.Empty;
            settings.FikenOAuthExpiresAtUtc = null;
            await WriteJsonAsync(_configPath, settings, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MappingsData> LoadMappingsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadMappingsUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveMappingsAsync(MappingsData mappings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        await _gate.WaitAsync(ct);
        try
        {
            var normalized = NormalizeMappings(mappings);
            await WriteJsonAsync(_mappingsPath, normalized, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveGroupMappingAsync(string groupName, long contactId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            throw new ArgumentException("Gruppenavn kan ikke være tomt.", nameof(groupName));
        }

        await _gate.WaitAsync(ct);
        try
        {
            var mappings = await LoadMappingsUnlockedAsync(ct);
            mappings.GroupNameToContactId[groupName.Trim()] = contactId;
            await WriteJsonAsync(_mappingsPath, mappings, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveGroupMappingAsync(string groupName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var mappings = await LoadMappingsUnlockedAsync(ct);
            if (mappings.GroupNameToContactId.Remove(groupName.Trim()))
            {
                await WriteJsonAsync(_mappingsPath, mappings, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveEventAsync(SavedMinSpeidingEvent savedEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(savedEvent);
        if (string.IsNullOrWhiteSpace(savedEvent.ProjectId))
        {
            throw new ArgumentException("Prosjekt-ID må fylles ut.", nameof(savedEvent));
        }

        await _gate.WaitAsync(ct);
        try
        {
            var mappings = await LoadMappingsUnlockedAsync(ct);
            var trimmedId = savedEvent.ProjectId.Trim();
            var trimmedName = string.IsNullOrWhiteSpace(savedEvent.Name)
                ? $"Arrangement {trimmedId}"
                : savedEvent.Name.Trim();
            var normalized = savedEvent with
            {
                Name = trimmedName,
                ProjectId = trimmedId,
                ApiKey = string.IsNullOrWhiteSpace(savedEvent.ApiKey) ? null : savedEvent.ApiKey.Trim(),
                QuestionsApiKey = string.IsNullOrWhiteSpace(savedEvent.QuestionsApiKey) ? null : savedEvent.QuestionsApiKey.Trim()
            };

            var existingIndex = mappings.SavedEvents.FindIndex(e =>
                string.Equals(e.ProjectId, trimmedId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                mappings.SavedEvents[existingIndex] = normalized;
            }
            else
            {
                mappings.SavedEvents.Add(normalized);
            }

            await WriteJsonAsync(_mappingsPath, mappings, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveEventAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var mappings = await LoadMappingsUnlockedAsync(ct);
            var removed = mappings.SavedEvents.RemoveAll(e =>
                string.Equals(e.ProjectId, projectId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                await WriteJsonAsync(_mappingsPath, mappings, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SavedEventDraftState?> LoadEventDraftStateAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var raw = await ReadJsonAsync<SavedEventDraftState>(GetDraftPath(projectId), ct);
            return raw is null ? null : NormalizeDraftState(raw, projectId.Trim());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveEventDraftStateAsync(SavedEventDraftState draftState, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draftState);
        if (string.IsNullOrWhiteSpace(draftState.ProjectId))
        {
            throw new ArgumentException("Prosjekt-ID må fylles ut.", nameof(draftState));
        }

        await _gate.WaitAsync(ct);
        try
        {
            var normalized = NormalizeDraftState(draftState, draftState.ProjectId.Trim());
            normalized.SavedAtUtc = DateTimeOffset.UtcNow;
            await WriteJsonAsync(GetDraftPath(normalized.ProjectId), normalized, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteEventDraftStateAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var path = GetDraftPath(projectId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetDraftPath(string projectId)
        => Path.Combine(DataDirectory, "drafts", $"{SanitizeFileKey(projectId)}.json");

    private static string SanitizeFileKey(string projectId)
    {
        var trimmed = projectId.Trim();
        var chars = new char[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            chars[i] = char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_';
        }

        var cleaned = new string(chars);
        return string.IsNullOrWhiteSpace(cleaned) ? "event" : cleaned;
    }

    private static SavedEventDraftState NormalizeDraftState(SavedEventDraftState source, string projectId)
    {
        var categoryPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (source.CategoryPricesNok is not null)
        {
            foreach (var (key, value) in source.CategoryPricesNok)
            {
                if (!string.IsNullOrWhiteSpace(key) && value >= 0m)
                {
                    categoryPrices[key.Trim()] = value;
                }
            }
        }

        var categoryLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source.CategoryLabels is not null)
        {
            foreach (var (key, value) in source.CategoryLabels)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    categoryLabels[key.Trim()] = value.Trim();
                }
            }
        }

        var excluded = (source.ExcludedCategoryKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var overrides = new Dictionary<string, ParticipantOverride>(StringComparer.OrdinalIgnoreCase);
        if (source.ParticipantOverrides is not null)
        {
            foreach (var (key, ov) in source.ParticipantOverrides)
            {
                if (!string.IsNullOrWhiteSpace(key) && ov is not null)
                {
                    overrides[key.Trim()] = ov;
                }
            }
        }

        var manualContacts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (source.ManualRecipientContactIds is not null)
        {
            foreach (var (key, value) in source.ManualRecipientContactIds)
            {
                if (!string.IsNullOrWhiteSpace(key) && value > 0)
                {
                    manualContacts[key.Trim()] = value;
                }
            }
        }

        return new SavedEventDraftState
        {
            ProjectId = projectId,
            SavedAtUtc = source.SavedAtUtc,
            FikenProjectId = source.FikenProjectId,
            IncomeAccount = string.IsNullOrWhiteSpace(source.IncomeAccount) ? null : source.IncomeAccount.Trim(),
            BankAccountNumber = string.IsNullOrWhiteSpace(source.BankAccountNumber) ? null : source.BankAccountNumber.Trim(),
            IssueDate = source.IssueDate,
            DaysUntilDueDate = source.DaysUntilDueDate,
            InvoiceText = source.InvoiceText,
            OurReference = source.OurReference,
            OrderReferencePrefix = source.OrderReferencePrefix,
            BillingMode = source.BillingMode,
            GroupLineMode = source.GroupLineMode,
            Filters = source.Filters ?? new ParticipantFilterOptions(),
            CategoryPricesNok = categoryPrices,
            CategoryLabels = categoryLabels,
            ExcludedCategoryKeys = excluded,
            ParticipantOverrides = overrides,
            ManualRecipientContactIds = manualContacts
        };
    }

    private void ApplyDefaults(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FikenApiToken) && !string.IsNullOrWhiteSpace(_defaults.FikenApiToken))
        {
            settings.FikenApiToken = _defaults.FikenApiToken.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.FikenOAuthClientId) && !string.IsNullOrWhiteSpace(_defaults.FikenOAuthClientId))
        {
            settings.FikenOAuthClientId = _defaults.FikenOAuthClientId.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.FikenOAuthClientSecret) && !string.IsNullOrWhiteSpace(_defaults.FikenOAuthClientSecret))
        {
            settings.FikenOAuthClientSecret = _defaults.FikenOAuthClientSecret.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.FikenCompanySlug) && !string.IsNullOrWhiteSpace(_defaults.FikenCompanySlug))
        {
            settings.FikenCompanySlug = _defaults.FikenCompanySlug.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.MinSpeidingBaseUrl))
        {
            settings.MinSpeidingBaseUrl = !string.IsNullOrWhiteSpace(_defaults.MinSpeidingBaseUrl)
                ? _defaults.MinSpeidingBaseUrl.Trim().TrimEnd('/')
                : "https://min.speiding.no/api";
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultIncomeAccount))
        {
            settings.DefaultIncomeAccount = "3200";
        }
    }

    private async Task<MappingsData> LoadMappingsUnlockedAsync(CancellationToken ct)
    {
        var raw = await ReadJsonAsync<MappingsData>(_mappingsPath, ct);
        return raw is null ? new MappingsData() : NormalizeMappings(raw);
    }

    private static MappingsData NormalizeMappings(MappingsData source)
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (source.GroupNameToContactId is not null)
        {
            foreach (var (key, value) in source.GroupNameToContactId)
            {
                if (!string.IsNullOrWhiteSpace(key) && value > 0)
                {
                    dict[key.Trim()] = value;
                }
            }
        }

        var events = (source.SavedEvents ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.ProjectId))
            .Select(e => e with
            {
                Name = string.IsNullOrWhiteSpace(e.Name) ? $"Arrangement {e.ProjectId.Trim()}" : e.Name.Trim(),
                ProjectId = e.ProjectId.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(e.ApiKey) ? null : e.ApiKey.Trim(),
                QuestionsApiKey = string.IsNullOrWhiteSpace(e.QuestionsApiKey) ? null : e.QuestionsApiKey.Trim()
            })
            .ToList();

        return new MappingsData
        {
            GroupNameToContactId = dict,
            SavedEvents = events
        };
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path) ?? DataDirectory;
        Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
