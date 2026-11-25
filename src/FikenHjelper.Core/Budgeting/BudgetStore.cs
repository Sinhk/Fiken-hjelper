using System.Text.Json;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Budgeting;

public interface IBudgetStore
{
    Task<BudgetData> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(BudgetData data, CancellationToken ct = default);
}

public sealed class JsonBudgetStore : IBudgetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _path;

    public JsonBudgetStore(string dataDirectory)
    {
        _directory = dataDirectory;
        _path = Path.Combine(dataDirectory, "budgets.json");
    }

    public async Task<BudgetData> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
            {
                return new BudgetData();
            }

            await using var stream = File.OpenRead(_path);
            var data = await JsonSerializer.DeserializeAsync<BudgetData>(stream, JsonOptions, ct);
            return Normalize(data ?? new BudgetData());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(BudgetData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var normalized = Normalize(data);

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory);
            var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, ct);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BudgetData Normalize(BudgetData data) => new()
    {
        Years = (data.Years ?? [])
            .Where(y => y is not null && y.Year is >= BudgetData.MinYear and <= BudgetData.MaxYear)
            .GroupBy(y => y.Year)
            .Select(g => g.Last())
            .OrderByDescending(y => y.Year)
            .Select(y => new BudgetYear
            {
                Year = y.Year,
                Lines = (y.Lines ?? [])
                    .Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Name))
                    .Select(l => new BudgetLine
                    {
                        Name = l.Name.Trim(),
                        Amount = l.Amount,
                        ManualActual = l.ManualActual,
                        ProjectIds = (l.ProjectIds ?? []).Where(id => id > 0).Distinct().ToList()
                    })
                    .ToList()
            })
            .ToList()
    };
}
