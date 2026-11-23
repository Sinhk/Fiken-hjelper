using System.Text.Json;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Import;

public interface IImportProfileStore
{
    Task<IReadOnlyList<ImportProfile>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Lagrer profilen (erstatter eksisterende profil med samme navn).</summary>
    Task SaveAsync(ImportProfile profile, CancellationToken ct = default);

    Task DeleteAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// Lagrer importprofiler lokalt i <c>import-profiles.json</c> ved siden av <c>mappings.json</c>.
/// Profilene inneholder bare kolonnenavn og regler – aldri persondata.
/// </summary>
public sealed class JsonImportProfileStore : IImportProfileStore
{
    public const string FileName = "import-profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _path;

    public JsonImportProfileStore(string dataDirectory)
    {
        _directory = dataDirectory;
        _path = Path.Combine(dataDirectory, FileName);
    }

    public async Task<IReadOnlyList<ImportProfile>> LoadAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await ReadAsync(ct)).Profiles
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ImportProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Profilen må ha et navn.", nameof(profile));
        }

        await _gate.WaitAsync(ct);
        try
        {
            var data = await ReadAsync(ct);
            var copy = profile.Clone();
            copy.Name = copy.Name.Trim();
            data.Profiles.RemoveAll(p => string.Equals(p.Name, copy.Name, StringComparison.OrdinalIgnoreCase));
            data.Profiles.Add(copy);
            await WriteAsync(data, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var data = await ReadAsync(ct);
            if (data.Profiles.RemoveAll(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
            {
                await WriteAsync(data, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImportProfilesData> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return new ImportProfilesData();
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<ImportProfilesData>(stream, JsonOptions, ct)
            ?? new ImportProfilesData();
    }

    private async Task WriteAsync(ImportProfilesData data, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions, ct);
        }

        File.Move(tempPath, _path, overwrite: true);
    }
}
