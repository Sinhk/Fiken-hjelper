using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Web.Components.Budgeting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace FikenHjelper.Web.Components.Pages;

public sealed partial class BudgetSetup : ComponentBase, IDisposable
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
    private bool isLoadingProjects;
    private bool projectsLoaded;
    private string? errorMessage;
    private readonly CancellationTokenSource disposeCts = new();

    private List<FikenProject> projects = [];
    private Dictionary<long, FikenProject> projectsById = [];
    private string companySlug = string.Empty;

    // Posteringer per prosjekt og år, til forslag, beløp og fanen «Prosjekter → poster».
    private readonly Dictionary<int, IReadOnlyDictionary<long, ProjectActivity>> activityByYear = [];
    private bool isLoadingActivity;
    private int activeTab;
    private bool onlyUnlinked = true;
    private string? projectSearch;

    private BudgetImportResult? importPreview;
    private string importFileName = string.Empty;
    private readonly HashSet<int> importSelection = [];
    private readonly HashSet<ProjectImportSuggestion> acceptedSuggestions = [];
    private readonly Dictionary<ProjectImportSuggestion, long> suggestionChoices = [];
    private bool isImporting;

    protected override async Task OnInitializedAsync()
    {
        data = await BudgetStore.LoadAsync(disposeCts.Token);
        var initialYear = QueryYear is { } q && data.FindYear(q) is not null
            ? q
            : data.Years.Select(y => y.Year).DefaultIfEmpty(0).Max();
        SelectYearCore(initialYear);

        if (!RendererInfo.IsInteractive)
        {
            return;
        }

        isLoadingProjects = true;
        StateHasChanged();
        try
        {
            companySlug = await Reports.GetCompanySlugAsync(disposeCts.Token);
            hasCompany = !string.IsNullOrWhiteSpace(companySlug);
            if (hasCompany)
            {
                projects = (await Reports.GetProjectsAsync(companySlug, ct: disposeCts.Token))
                    .OrderBy(p => p.Completed)
                    .ThenBy(p => p.Name, StringComparer.Create(Nb, ignoreCase: true))
                    .ToList();
                projectsById = projects.GroupBy(p => p.ProjectId).ToDictionary(g => g.Key, g => g.First());
                projectsLoaded = true;
            }
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (FikenApiException ex)
        {
            Logger.LogWarning(ex, "Fiken-feil ved henting av prosjekter");
            errorMessage = $"Klarte ikke å hente prosjekter fra Fiken: {ex.UserMessage} Budsjettallene kan fortsatt redigeres.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å hente prosjekter fra Fiken");
            errorMessage = "Klarte ikke å hente prosjekter fra Fiken. Budsjettallene kan fortsatt redigeres.";
        }
        finally
        {
            isLoadingProjects = false;
        }

        if (projectsLoaded && current is not null)
        {
            await LoadActivityAsync(current.Year);
        }
    }

    private IReadOnlyDictionary<long, ProjectActivity>? CurrentActivity =>
        current is null ? null : activityByYear.GetValueOrDefault(current.Year);

    private IReadOnlyList<ProjectLinkRow> ProjectRows =>
        current is null || CurrentActivity is not { } activity
            ? []
            : ProjectLinking.BuildProjectRows(current, data.FindYear(current.Year - 1), projects, activity);

    /// <summary>Antall prosjekter med posteringer som ikke er koblet, til merket på fanen.</summary>
    private object? UnlinkedBadge
    {
        get
        {
            if (current is null || CurrentActivity is not { } activity)
            {
                return null;
            }

            var linked = current.Lines.SelectMany(l => l.ProjectIds).ToHashSet();
            var count = activity.Count(kv => kv.Value.PostingCount > 0 && !linked.Contains(kv.Key) && projectsById.ContainsKey(kv.Key));
            return count > 0 ? count : null;
        }
    }

    private async Task LoadActivityAsync(int year, bool refresh = false)
    {
        if (!projectsLoaded || year == 0 || (!refresh && activityByYear.ContainsKey(year)))
        {
            return;
        }

        isLoadingActivity = true;
        StateHasChanged();
        try
        {
            var entries = await Reports.GetJournalEntriesAsync(companySlug, year, refresh, disposeCts.Token);
            activityByYear[year] = BudgetReportBuilder.SumByProject(entries);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Klarte ikke å hente posteringer for {Year}", year);
            activityByYear.Remove(year);
        }
        finally
        {
            isLoadingActivity = false;
        }
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
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
        await LoadActivityAsync(year);
    }

    private async Task AddYearAsync()
    {
        if (newYear is < BudgetData.MinYear or > BudgetData.MaxYear)
        {
            Snackbar.Add($"Velg et år mellom {BudgetData.MinYear} og {BudgetData.MaxYear}.", Severity.Warning);
            return;
        }

        if (data.FindYear(newYear) is null)
        {
            data.Years.Add(new BudgetYear { Year = newYear });
            data.Years.Sort((a, b) => b.Year.CompareTo(a.Year));
            MarkDirty();
        }

        SelectYearCore(newYear);
        await LoadActivityAsync(newYear);
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

    private void RemoveLine(BudgetLine line)
    {
        if (current?.Lines.Remove(line) == true)
        {
            MarkDirty();
        }
    }

    private void Move(BudgetLine line, int delta)
    {
        if (current is null)
        {
            return;
        }

        var index = current.Lines.IndexOf(line);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= current.Lines.Count)
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

    private async Task OpenPickerAsync(BudgetLine line)
    {
        if (current is null)
        {
            return;
        }

        var parameters = new DialogParameters<ProjectPickerDialog>
        {
            { d => d.Line, line },
            { d => d.Year, current },
            { d => d.PreviousYear, data.FindYear(current.Year - 1) },
            { d => d.Projects, projects },
            { d => d.Activity, CurrentActivity }
        };
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true };
        var dialog = await Dialogs.ShowAsync<ProjectPickerDialog>($"Prosjekter for «{line.Name}»", parameters, options);
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: List<long> ids } && !ids.SequenceEqual(line.ProjectIds))
        {
            line.ProjectIds = ids;
            MarkDirty();
        }
    }

    /// <summary>Kobler prosjektet til posten med indeks <paramref name="lineIndex"/> (−1: ingen) og fjerner andre koblinger.</summary>
    private void SetProjectLine(long projectId, int lineIndex)
    {
        if (current is null)
        {
            return;
        }

        foreach (var line in current.Lines)
        {
            line.ProjectIds.Remove(projectId);
        }

        if (lineIndex >= 0 && lineIndex < current.Lines.Count)
        {
            current.Lines[lineIndex].ProjectIds.Add(projectId);
        }

        MarkDirty();
    }

    private void AcceptStrongSuggestions()
    {
        if (current is null)
        {
            return;
        }

        var accepted = 0;
        foreach (var row in ProjectRows.Where(r => r.Lines.Count == 0 && r.SuggestionIsStrong && r.Suggestion is not null))
        {
            row.Suggestion!.Item.ProjectIds.Add(row.Project.ProjectId);
            accepted++;
        }

        if (accepted > 0)
        {
            MarkDirty();
            Snackbar.Add($"Koblet {accepted} prosjekter. Husk å lagre.", Severity.Info);
        }
    }

    private static string LineMatchText(NameMatch<BudgetLine> match) => match.Reason switch
    {
        NameMatchReason.Exact => "samme navn",
        NameMatchReason.History => "koblet hit i fjor",
        _ => $"likhet {match.Score} %"
    };

    private void RemoveProject(BudgetLine line, long id)
    {
        line.ProjectIds.Remove(id);
        MarkDirty();
    }

    private string? UsedByOther(long projectId, BudgetLine line) =>
        current?.Lines.FirstOrDefault(l => !ReferenceEquals(l, line) && l.ProjectIds.Contains(projectId))?.Name;

    private string? UsedByOtherTitle(long projectId, BudgetLine line) =>
        UsedByOther(projectId, line) is { } other ? $"Også koblet til «{other}»" : null;

    private string ProjectName(long id) =>
        projectsById.TryGetValue(id, out var p) ? p.Name : $"Prosjekt {id}";

    private void MarkDirty() => isDirty = true;

    private bool TryValidate()
    {
        var errors = data.Validate();
        foreach (var error in errors)
        {
            Snackbar.Add(error, Severity.Warning);
        }

        return errors.Count == 0;
    }

    private async Task SaveAsync()
    {
        if (isSaving || !TryValidate())
        {
            return;
        }

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

            acceptedSuggestions.Clear();
            suggestionChoices.Clear();
            foreach (var suggestion in importPreview.Suggestions)
            {
                suggestionChoices[suggestion] = suggestion.Candidates[0].Item.ProjectId;
                if (suggestion.IsStrong)
                {
                    acceptedSuggestions.Add(suggestion);
                }
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

    private void ToggleSuggestion(ProjectImportSuggestion suggestion, bool accepted)
    {
        if (accepted)
        {
            acceptedSuggestions.Add(suggestion);
        }
        else
        {
            acceptedSuggestions.Remove(suggestion);
        }
    }

    private void ChooseSuggestion(ProjectImportSuggestion suggestion, long projectId)
    {
        suggestionChoices[suggestion] = projectId;
        acceptedSuggestions.Add(suggestion);
    }

    private async Task ApplyImportAsync()
    {
        if (importPreview is null || isSaving)
        {
            return;
        }

        if (isDirty
            && !await ConfirmAsync("Ulagrede endringer", "Importen lagres med en gang. Endringer du ikke har lagret, forkastes. Vil du fortsette?"))
        {
            return;
        }

        var chosen = importPreview.Years.Where(y => importSelection.Contains(y.Year)).ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        isSaving = true;
        try
        {
            // Start fra det som er lagret, så ulagrede endringer i andre år ikke følger med.
            var saved = await BudgetStore.LoadAsync(disposeCts.Token);
            var replaced = chosen.Count(y => saved.FindYear(y.Year) is not null);
            if (replaced > 0
                && !await ConfirmAsync("Erstatte eksisterende budsjett?", $"{replaced} år finnes allerede og blir erstattet av importen."))
            {
                return;
            }

            BudgetWorkbookImporter.ApplySuggestions(
                importPreview,
                acceptedSuggestions.Select(s => (s, suggestionChoices[s])));
            saved.Years.RemoveAll(y => chosen.Any(c => c.Year == y.Year));
            saved.Years.AddRange(chosen);
            await BudgetStore.SaveAsync(saved, disposeCts.Token);
            data = await BudgetStore.LoadAsync(disposeCts.Token);
            isDirty = false;
            importPreview = null;
            SelectYearCore(chosen.Max(y => y.Year));
            await LoadActivityAsync(selectedYear);
            Snackbar.Add($"Importerte {chosen.Count} år.", Severity.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Klarte ikke å lagre importert budsjett");
            Snackbar.Add("Klarte ikke å lagre importen. Se loggen for detaljer.", Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task OnBeforeInternalNavigation(LocationChangingContext context)
    {
        if (isDirty && !await ConfirmAsync("Ulagrede endringer", "Du har endringer som ikke er lagret. Vil du forlate siden og forkaste dem?"))
        {
            context.PreventNavigation();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = await Dialogs.ShowMessageBoxAsync(title, message, yesText: "Ja", cancelText: "Avbryt");
        return result == true;
    }
}
