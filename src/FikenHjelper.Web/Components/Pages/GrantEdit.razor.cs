using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Grants;
using FikenHjelper.Web.Components.Grants;
using FikenHjelper.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace FikenHjelper.Web.Components.Pages;

public partial class GrantEdit
{
    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");
    private readonly CancellationTokenSource disposeCts = new();
    private readonly HashSet<string> expanded = [];

    [Inject] private GrantService Service { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILogger<GrantEdit> Logger { get; set; } = default!;

    [Parameter] public Guid? Id { get; set; }

    private GrantData stored = new();
    private Grant? grant;
    private GrantFikenData fiken = GrantFikenData.None;
    private GrantReport? report;
    private IReadOnlyList<GrantIssue> shareIssues = [];
    private IReadOnlyList<string> validationErrors = [];
    private bool isNew;
    private bool notFound;
    private bool isDirty;
    private bool isSaving;
    private bool isLoading;
    private bool isConfirming;
    private bool fikenLoaded;
    private string? errorMessage;
    private string? fikenMessage;
    private Guid? loadedFor;

    private string? SafeLink =>
        Uri.TryCreate(grant?.Link?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    protected override async Task OnParametersSetAsync()
    {
        var wanted = Id ?? Guid.Empty;
        if (loadedFor == wanted && (fikenLoaded || !RendererInfo.IsInteractive))
        {
            return;
        }

        if (loadedFor != wanted)
        {
            loadedFor = wanted;
            fikenLoaded = false;
            isDirty = false;
            validationErrors = [];
            errorMessage = null;
            fikenMessage = null;
            fiken = GrantFikenData.None;
            if (!await LoadGrantAsync())
            {
                return;
            }
        }

        if (RendererInfo.IsInteractive && !fikenLoaded && grant is not null)
        {
            await LoadFikenAsync(refresh: false);
        }
    }

    private async Task<bool> LoadGrantAsync()
    {
        try
        {
            stored = await Service.LoadAsync(disposeCts.Token);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å lese tilskuddsfilen");
            errorMessage = UserErrors.Describe(ex, "Klarte ikke å lese tilskuddene", "Tilskuddsfilen (grants.json i datamappen)");
            grant = null;
            return false;
        }

        isNew = Id is null;
        notFound = false;
        if (isNew)
        {
            grant = new Grant();
        }
        else if (stored.Find(Id!.Value) is { } existing)
        {
            grant = GrantCloner.Clone(existing);
        }
        else
        {
            grant = null;
            notFound = true;
            return false;
        }

        Recalculate();
        return true;
    }

    private async Task LoadFikenAsync(bool refresh)
    {
        if (grant is null)
        {
            return;
        }

        isLoading = true;
        fikenMessage = null;
        StateHasChanged();
        try
        {
            fiken = await Service.LoadFikenAsync(GrantReportBuilder.YearsToFetch(grant, Service.CurrentYear), refresh, disposeCts.Token);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Klarte ikke å hente Fiken-data for tilskudd");
            fiken = GrantFikenData.None;
            fikenMessage = UserErrors.Describe(ex, "Klarte ikke å hente data fra Fiken")
                + " Du kan fortsatt redigere tilskuddet, men «brukt» og forslag til utbetalinger vises ikke.";
        }
        finally
        {
            isLoading = false;
            fikenLoaded = true;
        }

        Recalculate();
    }

    private async Task EnsureYearsAsync()
    {
        if (grant is null || !fiken.HasCompany || fiken.Entries is null)
        {
            return;
        }

        var needed = GrantReportBuilder.YearsToFetch(grant, Service.CurrentYear);
        if (needed.All(fiken.Years.Contains))
        {
            return;
        }

        isLoading = true;
        StateHasChanged();
        try
        {
            await Service.EnsureYearsAsync(fiken, needed, ct: disposeCts.Token);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Klarte ikke å hente flere år fra Fiken");
            fikenMessage = UserErrors.Describe(ex, "Klarte ikke å hente flere år fra Fiken");
        }
        finally
        {
            isLoading = false;
        }
    }

    private void Recalculate()
    {
        if (grant is null)
        {
            report = null;
            shareIssues = [];
            return;
        }

        var all = stored.Grants;
        report = GrantReportBuilder.Build(grant, all, fiken.Entries, fiken.Accounts, fiken.Projects);
        shareIssues = report.Issues.Where(i => i.Kind is GrantIssueKind.ShareMissing or GrantIssueKind.ShareOverAllocated).ToList();
    }

    /// <summary>Gjør en endring i det redigerte tilskuddet og regner på nytt (og henter flere år fra Fiken ved behov).</summary>
    private async Task SetAsync(Action change)
    {
        change();
        isDirty = true;
        validationErrors = [];
        await EnsureYearsAsync();
        Recalculate();
    }

    private async Task SetAsync(Func<bool> change) => await SetAsync(() => change());

    private Task SetShareAsync(long projectId, decimal? share) => SetAsync(() =>
    {
        grant!.ProjectShares ??= [];
        if (share is { } s and > 0)
        {
            grant.ProjectShares[projectId] = s;
        }
        else
        {
            grant.ProjectShares.Remove(projectId);
        }
    });

    private Task RemoveProjectAsync(long projectId) => SetAsync(() =>
    {
        grant!.ProjectIds.Remove(projectId);
        grant.ProjectShares?.Remove(projectId);
    });

    private string ProjectName(long projectId) =>
        fiken.Projects.FirstOrDefault(p => p.ProjectId == projectId)?.Name ?? $"Prosjekt {projectId}";

    /// <summary>Andre aktive prosjekttilskudd (fra lagret data) som bruker samme prosjekt.</summary>
    private IReadOnlyList<Grant> OtherGrantsOnProject(long projectId) =>
        stored.Grants
            .Where(g => g.Id != grant!.Id && g.IsActive && g.Kind == GrantKind.Project && g.ProjectIds.Contains(projectId))
            .ToList();

    private string EntryNumber(long? journalEntryId) =>
        fiken.Entries?.FirstOrDefault(e => e.JournalEntryId == journalEntryId)?.JournalEntryNumber?.ToString(Nb) ?? "";

    private async Task PickProjectsAsync()
    {
        if (grant is null)
        {
            return;
        }

        var usedBy = stored.Grants
            .Where(g => g.Id != grant.Id && g.IsActive && g.Kind == GrantKind.Project)
            .SelectMany(g => g.ProjectIds.Select(id => (Id: id, Grant: g)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => $"«{x.Grant.DisplayName}»")));
        var parameters = new DialogParameters<GrantProjectPickerDialog>
        {
            { d => d.GrantName, grant.Name },
            { d => d.Projects, fiken.Projects },
            { d => d.Selected, grant.ProjectIds.ToList() },
            { d => d.UsedBy, usedBy }
        };
        var dialog = await Dialogs.ShowAsync<GrantProjectPickerDialog>($"Prosjekter for «{grant.DisplayName}»", parameters, DialogOptionsFor());
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: List<long> ids } && !ids.SequenceEqual(grant.ProjectIds))
        {
            await SetAsync(() =>
            {
                foreach (var removed in grant.ProjectIds.Except(ids).ToList())
                {
                    grant.ProjectShares?.Remove(removed);
                }

                grant.ProjectIds = ids;
            });
        }
    }

    private async Task FindPaymentsAsync()
    {
        if (grant is null || fiken.Entries is null)
        {
            return;
        }

        var suggestions = GrantPaymentSuggester.Suggest(grant, stored.Grants, fiken.Entries);
        var parameters = new DialogParameters<GrantPaymentSuggestionsDialog> { { d => d.Suggestions, suggestions } };
        var dialog = await Dialogs.ShowAsync<GrantPaymentSuggestionsDialog>("Finn utbetaling i Fiken", parameters, DialogOptionsFor(MaxWidth.ExtraLarge));
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: List<GrantPaymentSuggestion> chosen } && chosen.Count > 0)
        {
            await SetAsync(() =>
            {
                foreach (var s in chosen.Where(s => !grant.Payments.Any(p => p.JournalEntryId == s.JournalEntryId)))
                {
                    grant.Payments.Add(new GrantPayment
                    {
                        JournalEntryId = s.JournalEntryId,
                        Date = DateOnly.TryParseExact(s.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null
                    });
                }
            });
        }
    }

    private async Task SaveAsync()
    {
        if (grant is null || isSaving)
        {
            return;
        }

        isSaving = true;
        validationErrors = [];
        try
        {
            var data = await Service.LoadAsync(disposeCts.Token);
            var toSave = Normalized(grant);
            var index = data.Grants.FindIndex(g => g.Id == toSave.Id);
            if (index >= 0)
            {
                data.Grants[index] = toSave;
            }
            else
            {
                data.Grants.Add(toSave);
            }

            var errors = await Service.SaveAsync(data, disposeCts.Token);
            if (errors.Count > 0)
            {
                validationErrors = errors;
                return;
            }

            stored = data;
            grant = GrantCloner.Clone(toSave);
            isDirty = false;
            Snackbar.Add("Tilskuddet er lagret.", Severity.Success);
            Recalculate();
            if (isNew)
            {
                Navigation.NavigateTo($"tilskudd/{toSave.Id}", replace: true);
            }
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å lagre tilskudd");
            Snackbar.Add(UserErrors.Describe(ex, "Klarte ikke å lagre tilskuddet", "Tilskuddsfilen (grants.json i datamappen)"), Severity.Error, o => o.RequireInteraction = true);
        }
        finally
        {
            isSaving = false;
        }
    }

    /// <summary>Kopi som lagres: felt som ikke gjelder typen tømmes, og tekst trimmes.</summary>
    private static Grant Normalized(Grant source)
    {
        var copy = GrantCloner.Clone(source);
        copy.Name = copy.Name.Trim();
        copy.Grantor = copy.Grantor.Trim();
        copy.Scheme = string.IsNullOrWhiteSpace(copy.Scheme) ? null : copy.Scheme.Trim();
        copy.Link = string.IsNullOrWhiteSpace(copy.Link) ? null : copy.Link.Trim();
        copy.Notes = string.IsNullOrWhiteSpace(copy.Notes) ? null : copy.Notes.Trim();
        if (copy.Kind == GrantKind.Operating)
        {
            copy.ProjectIds = [];
            copy.ProjectShares = null;
        }
        else
        {
            copy.AccountRanges = [];
            copy.ManualUses = [];
            copy.ProjectShares = copy.ProjectShares?.Where(kv => copy.ProjectIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (copy.ProjectShares is { Count: 0 })
            {
                copy.ProjectShares = null;
            }
        }

        return copy;
    }

    private async Task DeleteAsync()
    {
        if (grant is null || isNew)
        {
            return;
        }

        if (!await ConfirmAsync("Slett tilskudd", $"Vil du slette «{grant.DisplayName}»? Det kan ikke angres. Posteringene i Fiken påvirkes ikke."))
        {
            return;
        }

        isSaving = true;
        try
        {
            var data = await Service.LoadAsync(disposeCts.Token);
            data.Grants.RemoveAll(g => g.Id == grant.Id);
            var errors = await Service.SaveAsync(data, disposeCts.Token);
            if (errors.Count > 0)
            {
                validationErrors = errors;
                return;
            }

            isDirty = false;
            Snackbar.Add("Tilskuddet er slettet.", Severity.Info);
            Navigation.NavigateTo("tilskudd");
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Klarte ikke å slette tilskudd");
            Snackbar.Add(UserErrors.Describe(ex, "Klarte ikke å slette tilskuddet", "Tilskuddsfilen (grants.json i datamappen)"), Severity.Error, o => o.RequireInteraction = true);
        }
        finally
        {
            isSaving = false;
        }
    }

    private void Toggle(string key)
    {
        if (!expanded.Remove(key))
        {
            expanded.Add(key);
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
        if (isConfirming)
        {
            return false;
        }

        isConfirming = true;
        try
        {
            return await Dialogs.ShowMessageBoxAsync(title, message, yesText: "Ja", cancelText: "Avbryt") == true;
        }
        finally
        {
            isConfirming = false;
        }
    }

    private static DialogOptions DialogOptionsFor(MaxWidth width = MaxWidth.Small) => new()
    {
        MaxWidth = width,
        FullWidth = true,
        CloseOnEscapeKey = true,
        BackdropClick = false,
        CloseButton = true
    };

    private static string Kr(decimal value) => value.ToString("N2", Nb);

    private static string KrOrDash(decimal? value) => value is { } v ? v.ToString("N2", Nb) : "–";

    private static string FormatDate(string? date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd.MM.yyyy", Nb)
            : date ?? string.Empty;

    public void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
    }
}
