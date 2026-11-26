using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Web.Components.Budgeting;
using FikenHjelper.Web.Components.Shared;
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

    // Økes hver gang ulagrede endringer forkastes og budsjettet lastes på nytt fra filen. «Angre» som ble
    // tilbudt før det, avvises, så en slettet post ikke settes inn igjen i et budsjett der den finnes fra før.
    private int discardGeneration;
    private bool isSaving;
    private bool isConfirming;
    private bool storeUnavailable;
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
    private readonly HashSet<int> loadingActivityYears = [];
    private readonly HashSet<int> failedActivityYears = [];
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
        if (!await LoadStoreAsync())
        {
            return;
        }

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
            errorMessage = $"{UserErrors.Describe(ex, "Klarte ikke å hente prosjekter fra Fiken")} Budsjettallene kan fortsatt redigeres.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å hente prosjekter fra Fiken");
            errorMessage = $"{UserErrors.Describe(ex, "Klarte ikke å hente prosjekter fra Fiken")} Budsjettallene kan fortsatt redigeres.";
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

    /// <summary>
    /// Leser budsjettet fra filen. Ved feil (skadet eller låst fil) vises en feilmelding og siden blir stående
    /// med tomt budsjett, og lagring sperres så en skadet fil ikke overskrives.
    /// </summary>
    private async Task<bool> LoadStoreAsync()
    {
        try
        {
            data = await BudgetStore.LoadAsync(disposeCts.Token);
            storeUnavailable = false;
            return true;
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å lese budsjettfilen");
            data = new BudgetData();
            current = null;
            selectedYear = 0;
            storeUnavailable = true;
            errorMessage = UserErrors.Describe(ex, "Klarte ikke å lese budsjettet");
            return false;
        }
    }

    private async Task ReloadStoreAsync()
    {
        errorMessage = null;
        discardGeneration++;
        if (!await LoadStoreAsync())
        {
            return;
        }

        SelectYearCore(data.Years.Select(y => y.Year).DefaultIfEmpty(0).Max());
        isDirty = false;
        await LoadActivityAsync(selectedYear);
    }

    private string PickerButtonText(BudgetLine line) =>
        isLoadingProjects ? "Henter prosjekter …"
        : !hasCompany ? "Velg foretak i Innstillinger"
        : projects.Count == 0 ? "Ingen prosjekter funnet"
        : line.ProjectIds.Count == 0 ? "Velg prosjekter"
        : "Endre";

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
            var count = activity.Count(kv => kv.Value.PostingCount > 0 && !linked.Contains(kv.Key) && projectsById.ContainsKey(kv.Key))
                + ProjectSplits.Validate(current, ProjectName).Count;
            return count > 0 ? count : null;
        }
    }

    private bool IsLoadingActivity => current is not null && loadingActivityYears.Contains(current.Year);

    private bool ActivityFailed => current is not null && failedActivityYears.Contains(current.Year);

    /// <summary>Henter posteringer for året. Hvert år hentes høyst én gang om gangen.</summary>
    private async Task LoadActivityAsync(int year, bool refresh = false)
    {
        if (!projectsLoaded
            || year == 0
            || loadingActivityYears.Contains(year)
            || (!refresh && activityByYear.ContainsKey(year)))
        {
            return;
        }

        loadingActivityYears.Add(year);
        failedActivityYears.Remove(year);
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
            failedActivityYears.Add(year);
        }
        finally
        {
            loadingActivityYears.Remove(year);
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
            discardGeneration++;
            if (!await LoadStoreAsync())
            {
                return;
            }

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
        await LoadActivityAsync(selectedYear);
    }

    private void AddLine()
    {
        current?.Lines.Add(new BudgetLine { Name = "Ny post" });
        MarkDirty();
    }

    private void RemoveLine(BudgetLine line)
    {
        if (current is null)
        {
            return;
        }

        var index = current.Lines.IndexOf(line);
        if (index < 0)
        {
            return;
        }

        var year = current.Year;
        current.Lines.RemoveAt(index);
        MarkDirty();

        var label = string.IsNullOrWhiteSpace(line.Name) ? "uten navn" : $"«{line.Name}»";
        var generation = discardGeneration;
        AddUndoSnackbar($"Slettet posten {label}.", () =>
            BudgetUndo.RestoreLine(data, year, index, line, discardedSince: generation != discardGeneration));
    }

    /// <summary>
    /// Viser en melding med «Angre». Brukes for slettinger som ellers ikke kan hentes tilbake før man har lagret.
    /// <paramref name="undo"/> returnerer null når angringen ble utført, ellers en feilmelding.
    /// </summary>
    private void AddUndoSnackbar(string message, Func<string?> undo)
    {
        Snackbar.Add(message, Severity.Info, config =>
        {
            config.Action = "Angre";
            config.ActionVariant = Variant.Filled;
            config.VisibleStateDuration = 8000;
            config.OnClick = _ =>
            {
                if (undo() is { } error)
                {
                    Snackbar.Add(error, Severity.Warning);
                }
                else
                {
                    MarkDirty();
                }

                return InvokeAsync(StateHasChanged);
            };
        });
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

        current.Lines.AddRange(previous.Copy().Lines.Select(l =>
        {
            l.ManualActual = null;
            return l;
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
        var dialog = await Dialogs.ShowAsync<ProjectPickerDialog>($"Prosjekter for «{line.Name}»", parameters, DialogOptionsFor());
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: List<long> ids } && !ids.SequenceEqual(line.ProjectIds))
        {
            foreach (var removed in line.ProjectIds.Except(ids))
            {
                line.SetShare(removed, null);
            }

            line.ProjectIds = ids;
            MarkDirty();
        }
    }

    /// <summary>Felles valg for dialogene: ikke lukk ved klikk utenfor (mister lett valg), men med lukkeknapp og Esc.</summary>
    private static DialogOptions DialogOptionsFor() => new()
    {
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
        CloseOnEscapeKey = true,
        BackdropClick = false,
        CloseButton = true
    };

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
            line.SetShare(projectId, null);
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
            Snackbar.Add($"Koblet {accepted} {(accepted == 1 ? "prosjekt" : "prosjekter")}. Husk å lagre.", Severity.Info);
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
        var index = line.ProjectIds.IndexOf(id);
        if (index < 0)
        {
            return;
        }

        if (current is null)
        {
            return;
        }

        var year = current.Year;
        var share = line.ShareOf(id);
        var otherLines = current.Lines
            .Where(l => !ReferenceEquals(l, line) && l.ProjectIds.Contains(id))
            .Select(l => l.Name.Trim())
            .ToList();
        line.ProjectIds.RemoveAt(index);
        line.SetShare(id, null);
        MarkDirty();

        var generation = discardGeneration;
        var projectName = ProjectName(id);
        AddUndoSnackbar($"Fjernet koblingen til «{projectName}» fra «{line.Name}».", () =>
            BudgetUndo.RestoreProjectLink(
                data, year, line, id, index, share, otherLines,
                discardedSince: generation != discardGeneration,
                projectName));
    }

    /// <summary>Andelen som tekst når prosjektet er fordelt på flere poster, ellers null.</summary>
    private string? SharedShare(BudgetLine line, long id)
    {
        if (current is null || line.ManualActual.HasValue
            || current.Lines.Count(l => !l.ManualActual.HasValue && l.ProjectIds.Contains(id)) < 2)
        {
            return null;
        }

        return line.ShareOf(id) is { } share ? $"{share.ToString("0.##", Nb)} %" : "mangler andel";
    }

    private async Task OpenSplitAsync(ProjectLinkRow row)
    {
        if (current is null)
        {
            return;
        }

        var parameters = new DialogParameters<ProjectSplitDialog>
        {
            { d => d.Project, row.Project },
            { d => d.Year, current },
            { d => d.Amount, row.Activity.Amount }
        };
        var dialog = await Dialogs.ShowAsync<ProjectSplitDialog>($"Fordel «{row.Project.Name}»", parameters, DialogOptionsFor());
        var result = await dialog.Result;
        if (result is not { Canceled: false, Data: List<ProjectSplitDialog.SplitRow> rows })
        {
            return;
        }

        var id = row.Project.ProjectId;
        // Koblinger til poster med manuelt regnskapstall er ikke med i dialogen og beholdes.
        foreach (var line in current.Lines.Where(l => !l.ManualActual.HasValue))
        {
            line.ProjectIds.Remove(id);
            line.SetShare(id, null);
        }

        foreach (var split in rows)
        {
            split.Line.ProjectIds.Add(id);
            split.Line.SetShare(id, rows.Count > 1 ? split.Share : null);
        }

        MarkDirty();
    }

    private string? UsedByOther(long projectId, BudgetLine line) =>
        current?.Lines.FirstOrDefault(l => !ReferenceEquals(l, line) && l.ProjectIds.Contains(projectId))?.Name;

    /// <summary>
    /// Advarsel for et prosjekt i en post: koblet til flere poster uten gyldig fordelingsnøkkel, eller koblet
    /// til en annen post når denne har manuelt regnskapstall. Null når alt er i orden.
    /// </summary>
    private string? ProjectChipWarning(long projectId, BudgetLine line)
    {
        if (current is null || UsedByOther(projectId, line) is not { } other)
        {
            return null;
        }

        var links = ProjectSplits.LinksByProject(current).GetValueOrDefault(projectId) ?? [];
        if (links.Count > 1)
        {
            return ProjectSplits.Check(projectId, links) is null ? null : "Fordelingsnøkkelen går ikke opp i 100 %. Rett den med «Fordel …».";
        }

        return $"Også koblet til «{other}»";
    }

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
        if (isSaving || storeUnavailable || !TryValidate())
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
            var issues = data.Years.SelectMany(y => ProjectSplits.Validate(y, ProjectName).Select(i => $"{y.Year}: {i.Description}")).ToList();
            if (issues.Count > 0)
            {
                Snackbar.Add(
                    $"Fordelingsnøkkel må rettes: {string.Join(" ", issues)}",
                    Severity.Warning,
                    o => o.RequireInteraction = true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å lagre budsjett");
            Snackbar.Add(UserErrors.Describe(ex, "Klarte ikke å lagre budsjettet"), Severity.Error, o => o.RequireInteraction = true);
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

        importPreview = null;
        if (file.Size > MaxImportBytes)
        {
            Snackbar.Add("Filen er for stor (maks 10 MB).", Severity.Error);
            return;
        }

        isImporting = true;
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
        catch (IOException ex) when (ex.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase))
        {
            // OpenReadStream avviser filer over grensen selv om Size ikke var kjent på forhånd.
            Logger.LogWarning(ex, "Importfil {FileName} er for stor", file.Name);
            Snackbar.Add("Filen er for stor (maks 10 MB).", Severity.Error);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å lese importfil {FileName}", file.Name);
            Snackbar.Add("Klarte ikke å lese filen. Sjekk at det er en .xlsx-fil lastet ned fra Google Sheets, og at den ikke er åpen i et annet program.", Severity.Error);
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
        if (importPreview is null || isSaving || isConfirming || storeUnavailable)
        {
            return;
        }

        if (isDirty
            && !await ConfirmAsync("Ulagrede endringer", "Importen lagres med en gang. Endringer du ikke har lagret, forkastes. Vil du fortsette?"))
        {
            return;
        }

        // Kopier, så forhåndsvisningen er uendret hvis lagringen feiler og brukeren endrer valgene.
        var chosen = importPreview.Years.Where(y => importSelection.Contains(y.Year)).Select(y => y.Copy()).ToList();
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

            var splitMessages = BudgetWorkbookImporter.ApplySuggestions(
                chosen,
                acceptedSuggestions.Select(s => (s, suggestionChoices[s])));
            var notLinked = importPreview.Suggestions
                .Where(s => !acceptedSuggestions.Contains(s) && s.Targets.Any(t => importSelection.Contains(t.Year)))
                .Select(s => $"«{s.SheetName}»")
                .ToList();
            saved.Years.RemoveAll(y => chosen.Any(c => c.Year == y.Year));
            saved.Years.AddRange(chosen);
            await BudgetStore.SaveAsync(saved, disposeCts.Token);
            data = await BudgetStore.LoadAsync(disposeCts.Token);
            discardGeneration++;
            isDirty = false;
            importPreview = null;
            SelectYearCore(chosen.Max(y => y.Year));
            await LoadActivityAsync(selectedYear);
            Snackbar.Add($"Importerte {chosen.Count} år.", Severity.Success);
            if (notLinked.Count > 0)
            {
                Snackbar.Add(
                    $"Disse prosjektnavnene ble ikke koblet: {string.Join(", ", notLinked)}. Koble dem manuelt, f.eks. under «Prosjekter → poster».",
                    Severity.Warning,
                    o => o.RequireInteraction = true);
            }

            if (splitMessages.Count > 0)
            {
                Snackbar.Add(string.Join(" ", splitMessages), Severity.Info, o => o.RequireInteraction = true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Klarte ikke å lagre importert budsjett");
            Snackbar.Add(UserErrors.Describe(ex, "Klarte ikke å lagre importen"), Severity.Error, o => o.RequireInteraction = true);
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
        // Dobbeltklikk skal ikke åpne to dialoger oppå hverandre.
        if (isConfirming)
        {
            return false;
        }

        isConfirming = true;
        try
        {
            var result = await Dialogs.ShowMessageBoxAsync(title, message, yesText: "Ja", cancelText: "Avbryt");
            return result == true;
        }
        finally
        {
            isConfirming = false;
        }
    }
}
