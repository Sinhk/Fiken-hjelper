using System.Text.Json;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Grants;

public interface IGrantStore
{
    Task<GrantData> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(GrantData data, CancellationToken ct = default);
}

/// <summary>Lagrer tilskudd i <c>grants.json</c>. Samme mønster som <c>JsonBudgetStore</c>.</summary>
public sealed class JsonGrantStore : IGrantStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _path;

    public JsonGrantStore(string dataDirectory)
    {
        _directory = dataDirectory;
        _path = Path.Combine(dataDirectory, "grants.json");
    }

    public async Task<GrantData> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
            {
                return new GrantData();
            }

            await using var stream = File.OpenRead(_path);
            var data = await JsonSerializer.DeserializeAsync<GrantData>(stream, JsonOptions, ct);
            return Normalize(data ?? new GrantData());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(GrantData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var normalized = Normalize(data);

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory);
            var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = File.Create(tempPath))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, ct);
                }

                File.Move(tempPath, _path, overwrite: true);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static GrantData Normalize(GrantData data) => new()
    {
        Grants = (data.Grants ?? [])
            .Where(g => g is not null)
            .Select(NormalizeGrant)
            .GroupBy(g => g.Id)
            .Select(g => g.Last())
            .ToList()
    };

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Grant NormalizeGrant(Grant g)
    {
        var ids = (g.ProjectIds ?? []).Where(id => id > 0).Distinct().ToList();
        // Andeler beholdes bare for koblede prosjekter. Ugyldige verdier (0 eller over 100) beholdes med vilje,
        // så Grant.Validate() kan melde feilen. Validate skal kjøres før lagring; normaliseringen skjuler ikke feil.
        var shares = (g.ProjectShares ?? [])
            .Where(kv => ids.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var ranges = (g.AccountRanges ?? [])
            .Where(r => r is not null && r.From > 0 && r.To > 0)
            .Select(r => new AccountRange { From = Math.Min(r.From, r.To), To = Math.Max(r.From, r.To) })
            .DistinctBy(r => (r.From, r.To))
            .ToList();

        var uses = (g.ManualUses ?? [])
            .Where(u => u is not null && u.Amount != 0)
            .Select(u => new GrantManualUse { Date = u.Date, Amount = u.Amount, Description = Trimmed(u.Description) })
            .ToList();

        var seenEntries = new HashSet<long>();
        var payments = (g.Payments ?? [])
            .Where(p => p is not null)
            .Select(p => p.IsLinked
                // Koblede utbetalinger henter beløpet fra Fiken, så et eventuelt lagret beløp droppes.
                ? new GrantPayment { JournalEntryId = p.JournalEntryId, Date = p.Date, Note = Trimmed(p.Note) }
                : new GrantPayment { Date = p.Date, Amount = p.Amount, Note = Trimmed(p.Note) })
            .Where(p => p.IsLinked ? seenEntries.Add(p.JournalEntryId!.Value) : p.Amount is not null)
            .ToList();

        return new Grant
        {
            Id = g.Id == Guid.Empty ? Guid.NewGuid() : g.Id,
            Name = (g.Name ?? string.Empty).Trim(),
            Grantor = (g.Grantor ?? string.Empty).Trim(),
            Scheme = Trimmed(g.Scheme),
            Kind = g.Kind,
            Status = g.Status,
            ApplicationDeadline = g.ApplicationDeadline,
            AppliedDate = g.AppliedDate,
            DecisionDate = g.DecisionDate,
            ReportDeadline = g.ReportDeadline,
            UseFrom = g.UseFrom,
            UseTo = g.UseTo,
            AppliedAmount = g.AppliedAmount,
            GrantedAmount = g.GrantedAmount,
            ProjectIds = ids,
            ProjectShares = shares.Count > 0 ? shares : null,
            AccountRanges = ranges,
            ManualUses = uses,
            Payments = payments,
            Link = Trimmed(g.Link),
            Notes = Trimmed(g.Notes)
        };
    }
}
