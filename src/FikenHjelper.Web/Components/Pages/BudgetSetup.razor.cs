using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace FikenHjelper.Web.Components.Pages;

public sealed partial class BudgetSetup : ComponentBase
{
    private const long MaxImportBytes = 10 * 1024 * 1024;
    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    [Inject] private IBudgetStore BudgetStore { get; set; } = default!;
    [Inject] private BudgetReportService Reports { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private ILogger<BudgetSetup> Logger { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "year")]
    private int? QueryYear { get; set; }

    private BudgetData data = new();
    private BudgetYear? current;
    private int selectedYear;
    private int newYear = DateTime.Today.Year + 1;
    private bool isDirty;
    private bool isSaving;
    private bool hasCompany = true;
    private string? errorMessage;

    private List<FikenProject> projects = [];
    private Dictionary<long, FikenProject> projectsById = [];

    private BudgetImportResult? importPreview;
    private string importFileName = string.Empty;
    private readonly HashSet<int> importSelection = [];
    private bool isImporting;

    protected override async Task OnInitializedAsync()
    {
        data = await BudgetStore.LoadAsync();
        var initialYear = QueryYear is { } q && data.FindYear(q) is not null
            ? q
            : data.Years.Select(y => y.Year).DefaultIfEmpty(0).Max();
        SelectYearCore(initialYear);

        if (!RendererInfo.IsInteractive)
        {
            return;
        }

        try
        {
            var slug = await Reports.GetCompanySlugAsync();
            hasCompany = !string.IsNullOrWhiteSpace(slug);
            if (hasCompany)
            {
                projects = (await Reports.GetProjectsAsync(slug))
                    .OrderBy(p => p.Completed)
                    .ThenBy(p => p.Name, StringComparer.Create(Nb, ignoreCase: true))
                    .ToList();
                projectsById = projects.GroupBy(p => p.ProjectId).ToDictionary(g => g.Key, g => g.First());
            }
        }
        catch (FikenApiException ex)
        {
            errorMessage = ex.Message;
        }
    }

    private void SelectYearCore(int year)
    {
        selectedYear = year;
        current = data.FindYear(year);
    }

    private async Task SelectYearAsync(int year)
    {
        if (isDirty && !await ConfirmAsync("Ulagrede endringer", "Du har endringer som ikke er lagret. Vil du forkaste dem?"))
        {
            return;
        }

        if (isDirty)
        {
            data = await BudgetStore.LoadAsync();
            isDirty = false;
        }

        SelectYearCore(year);
    }

    private async Task AddYearAsync()
    {
        if (newYear is < 2000 or > 2100)
        {
            Snackbar.Add("Velg et år mellom 2000 og 2100.", Severity.Warning);
            return;
        }

        if (data.FindYear(newYear) is null)
        {
            data.Years.Add(new BudgetYear { Year = newYear });
            data.Years.Sort((a, b) => b.Year.CompareTo(a.Year));
            MarkDirty();
        }

        SelectYearCore(newYear);
        await Task.CompletedTask;
    }

    private async Task DeleteYearAsync()
    {
        if (current is null
            || !await ConfirmAsync($"Slett {current.Year}?", $"Budsjettet for {current.Year} med {current.Lines.Count} poster slettes når du lagrer."))
        {
            return;
        }

        data.Years.Remove(current);
        MarkDirty();
        SelectYearCore(data.Years.Select(y => y.Year).DefaultIfEmpty(0).Max());
    }

    private void AddLine()
    {
        current?.Lines.Add(new BudgetLine { Name = "Ny post" });
        MarkDirty();
    }

    private void RemoveLine(int index)
    {
        current?.Lines.RemoveAt(index);
        MarkDirty();
    }

    private void Move(int index, int delta)
    {
        if (current is null)
        {
            return;
        }

        var target = index + delta;
        if (target < 0 || target >= current.Lines.Count)
        {
            return;
        }

        (current.Lines[index], current.Lines[target]) = (current.Lines[target], current.Lines[index]);
        MarkDirty();
    }

    private void CopyFrom(BudgetYear previous)
    {
        if (current is null || current.Lines.Count > 0)
        {
            return;
        }

        current.Lines.AddRange(previous.Lines.Select(l => new BudgetLine
        {
            Name = l.Name,
            Amount = l.Amount,
            ProjectIds = [.. l.ProjectIds]
        }));
        MarkDirty();
    }

    private void SetProjects(BudgetLine line, IEnumerable<long>? ids)
    {
        line.ProjectIds = (ids ?? []).Distinct().ToList();
        MarkDirty();
    }

    private void RemoveProject(BudgetLine line, long id)
    {
        line.ProjectIds.Remove(id);
        MarkDirty();
    }

    private string? UsedByOther(long projectId, BudgetLine line) =>
        current?.Lines.FirstOrDefault(l => !ReferenceEquals(l, line) && l.ProjectIds.Contains(projectId))?.Name;

    private string ProjectName(long id) =>
        projectsById.TryGetValue(id, out var p) ? p.Name : $"Prosjekt {id}";

    private string ProjectSummary(IReadOnlyList<string> selected) =>
        selected.Count == 0 ? string.Empty : string.Join(", ", selected);

    private void MarkDirty() => isDirty = true;

    private async Task SaveAsync()
    {
        isSaving = true;
        try
        {
            await BudgetStore.SaveAsync(data);
            data = await BudgetStore.LoadAsync();
            SelectYearCore(selectedYear);
            isDirty = false;
            Snackbar.Add("Budsjettet er lagret.", Severity.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å lagre budsjett");
            Snackbar.Add("Klarte ikke å lagre budsjettet. Se loggen for detaljer.", Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task ReadImportFileAsync(IBrowserFile? file)
    {
        if (file is null)
        {
            return;
        }

        isImporting = true;
        importPreview = null;
        try
        {
            await using var upload = file.OpenReadStream(MaxImportBytes);
            using var buffer = new MemoryStream();
            await upload.CopyToAsync(buffer);
            buffer.Position = 0;

            importPreview = BudgetWorkbookImporter.Import(buffer, projects);
            importFileName = file.Name;
            importSelection.Clear();
            foreach (var y in importPreview.Years)
            {
                importSelection.Add(y.Year);
            }
        }
        catch (InvalidDataException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å lese importfil {FileName}", file.Name);
            Snackbar.Add("Klarte ikke å lese filen. Sjekk at det er en .xlsx-fil.", Severity.Error);
        }
        finally
        {
            isImporting = false;
        }
    }

    private void ToggleImportYear(int year, bool selected)
    {
        if (selected)
        {
            importSelection.Add(year);
        }
        else
        {
            importSelection.Remove(year);
        }
    }

    private async Task ApplyImportAsync()
    {
        if (importPreview is null)
        {
            return;
        }

        var chosen = importPreview.Years.Where(y => importSelection.Contains(y.Year)).ToList();
        var replaced = chosen.Count(y => data.FindYear(y.Year) is not null);
        if (replaced > 0
            && !await ConfirmAsync("Erstatte eksisterende budsjett?", $"{replaced} år finnes allerede og blir erstattet av importen."))
        {
            return;
        }

        data.Years.RemoveAll(y => chosen.Any(c => c.Year == y.Year));
        data.Years.AddRange(chosen);
        data.Years.Sort((a, b) => b.Year.CompareTo(a.Year));

        await BudgetStore.SaveAsync(data);
        data = await BudgetStore.LoadAsync();
        isDirty = false;
        importPreview = null;
        SelectYearCore(chosen.Max(y => y.Year));
        Snackbar.Add($"Importerte {chosen.Count} år.", Severity.Success);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = await Dialogs.ShowMessageBoxAsync(title, message, yesText: "Ja", cancelText: "Avbryt");
        return result == true;
    }
}
