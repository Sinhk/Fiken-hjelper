using System.Globalization;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Invoicing;
using FikenHjelper.Core.MinSpeiding;
using Microsoft.AspNetCore.Components;

namespace FikenHjelper.Web.Components.Pages;

public sealed partial class EventInvoicing : ComponentBase, IAsyncDisposable
{
    [Inject] private ILocalConfigStore ConfigStore { get; set; } = default!;
    [Inject] private IFikenClient FikenClient { get; set; } = default!;
    [Inject] private IMinSpeidingClient MinSpeidingClient { get; set; } = default!;
    [Inject] private ILogger<EventInvoicing> Logger { get; set; } = default!;

    private AppSettings settings = new();
    private MappingsData mappings = new();
    private bool fikenTokenConfigured;

    private List<FikenCompany> companies = [];
    private List<FikenContact> customers = [];
    private List<FikenProject> projects = [];
    private List<FikenAccount> incomeAccounts = [];
    private List<FikenBankAccount> bankAccounts = [];
    private List<FikenInvoiceDraftSummary> existingDrafts = [];

    private string selectedCompanySlug = string.Empty;
    private long? selectedProjectId;
    private string incomeAccount = "3200";
    private string bankAccountNumber = string.Empty;
    private DateOnly issueDate = DateOnly.FromDateTime(DateTime.Today);
    private int daysUntilDueDate = 14;
    private string invoiceText = string.Empty;
    private string ourReference = string.Empty;
    private string orderReferencePrefix = string.Empty;

    private string minSpeidingProjectId = string.Empty;
    private string minSpeidingApiKey = string.Empty;
    private string minSpeidingQuestionsApiKey = string.Empty;
    private string eventNameToSave = string.Empty;
    private bool rememberEvent;
    private bool saveApiKeyLocally;

    private BillingMode billingMode = BillingMode.PerScoutGroup;
    private GroupLineMode groupLineMode = GroupLineMode.OneLinePerParticipant;
    private ParticipantFilterOptions filters = new();

    private MinSpeidingEventLoadResult? eventData;
    private readonly Dictionary<string, decimal> categoryPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> manualCategoryPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> categoryLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedCategoryKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParticipantOverride> participantOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> manualRecipientContactIds = new(StringComparer.OrdinalIgnoreCase);
    private InvoicePlanResult? invoicePlan;
    private List<DraftCreationItemResult> creationResults = [];
    private DateTimeOffset? lastDraftSavedAtLocal;
    private bool projectEditedBeforeLoad;

    private CancellationTokenSource? fikenCts;
    private CancellationTokenSource? eventCts;
    private CancellationTokenSource? submitCts;
    private bool isLoadingFiken;
    private bool isLoadingEvent;
    private bool isSubmittingDrafts;
    private bool hasLoadedInteractive;
    private string? bannerMessage;
    private bool bannerIsError;

    protected override async Task OnInitializedAsync()
    {
        settings = await ConfigStore.LoadSettingsAsync();
        mappings = await ConfigStore.LoadMappingsAsync();
        fikenTokenConfigured = settings.IsFikenConfigured;

        selectedCompanySlug = settings.FikenCompanySlug;
        incomeAccount = settings.DefaultIncomeAccount;
        bankAccountNumber = settings.DefaultBankAccountNumber;
        daysUntilDueDate = settings.DefaultDaysUntilDueDate;
        ourReference = settings.DefaultOurReference;

        if (!RendererInfo.IsInteractive || hasLoadedInteractive || !fikenTokenConfigured)
        {
            return;
        }

        hasLoadedInteractive = true;
        await ReloadFikenContextAsync();
    }

    private async Task ReloadFikenContextAsync()
    {
        if (!fikenTokenConfigured)
        {
            SetBanner("Koble til Fiken med OAuth2 eller API-nøkkel under Innstillinger først.", isError: true);
            return;
        }

        isLoadingFiken = true;
        bannerMessage = null;
        var token = ResetFikenToken();

        try
        {
            companies = (await FikenClient.GetCompaniesAsync(ct: token)).ToList();
            if (string.IsNullOrWhiteSpace(selectedCompanySlug) && companies.Count > 0)
            {
                selectedCompanySlug = companies[0].Slug;
            }

            if (!string.IsNullOrWhiteSpace(selectedCompanySlug))
            {
                await LoadCompanyDataSequentiallyAsync(selectedCompanySlug, token);
            }

            RebuildPlan();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (FikenApiException ex)
        {
            Logger.LogWarning(ex, "Failed to load Fiken metadata");
            SetBanner(ex.UserMessage, isError: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error loading Fiken metadata");
            SetBanner("Kunne ikke hente data fra Fiken API.", isError: true);
        }
        finally
        {
            isLoadingFiken = false;
        }
    }

    private async Task LoadCompanyDataSequentiallyAsync(string companySlug, CancellationToken token)
    {
        customers = (await FikenClient.GetCustomersAsync(companySlug, token)).ToList();
        projects = (await FikenClient.GetProjectsAsync(companySlug, completed: false, ct: token)).ToList();
        incomeAccounts = (await FikenClient.GetIncomeAccountsAsync(companySlug, token)).ToList();
        bankAccounts = (await FikenClient.GetBankAccountsAsync(companySlug, token)).ToList();

        existingDrafts = (await FikenClient.GetInvoiceDraftsAsync(companySlug, token)).ToList();

        if (incomeAccounts.Count > 0 && !incomeAccounts.Any(a => a.Code == incomeAccount))
        {
            incomeAccount = incomeAccounts[0].Code;
        }

        if (string.IsNullOrWhiteSpace(bankAccountNumber) && bankAccounts.Count > 0)
        {
            bankAccountNumber = bankAccounts[0].BankAccountNumber ?? string.Empty;
        }
    }

    private async Task HandleCompanyChangedAsync(string newSlug)
    {
        // Drafts and customers being created belong to the previous company: stop that work first.
        submitCts?.Cancel();
        selectedCompanySlug = newSlug;
        selectedProjectId = null;
        // Customer IDs belong to the previous company and must not be reused (or saved) for the new one.
        manualRecipientContactIds.Clear();
        projectEditedBeforeLoad = false;
        customers = [];
        projects = [];
        incomeAccounts = [];
        bankAccounts = [];
        existingDrafts = [];

        if (!string.IsNullOrWhiteSpace(selectedCompanySlug) && fikenTokenConfigured)
        {
            isLoadingFiken = true;
            var token = ResetFikenToken();
            try
            {
                await LoadCompanyDataSequentiallyAsync(selectedCompanySlug, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (FikenApiException ex)
            {
                Logger.LogWarning(ex, "Failed to switch Fiken company");
                SetBanner(ex.UserMessage, isError: true);
            }
            finally
            {
                isLoadingFiken = false;
            }
        }

        RebuildPlan();
    }

    private async Task HandleProjectChanged(long? projectId)
    {
        selectedProjectId = projectId;
        if (eventData is not null)
        {
            await RebuildAndPersistAsync();
            return;
        }

        projectEditedBeforeLoad = true;
        if (!string.IsNullOrWhiteSpace(minSpeidingProjectId))
        {
            try
            {
                var existing = await ConfigStore.LoadEventDraftStateAsync(minSpeidingProjectId);
                if (existing is not null)
                {
                    existing.FikenProjectId = projectId;
                    existing.FikenCompanySlug = NullIfEmpty(selectedCompanySlug);
                    await ConfigStore.SaveEventDraftStateAsync(existing);
                    lastDraftSavedAtLocal = DateTimeOffset.Now;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to update saved Fiken project for {ProjectId}", minSpeidingProjectId);
            }
        }
    }

    private Task HandleIncomeAccountChanged(string value)
    {
        incomeAccount = value;
        return RebuildAndPersistAsync();
    }

    private Task HandleBankAccountChanged(string value)
    {
        bankAccountNumber = value;
        return RebuildAndPersistAsync();
    }

    private Task HandleIssueDateChanged(DateOnly value)
    {
        issueDate = value;
        return RebuildAndPersistAsync();
    }

    private Task HandleDueDaysChanged(int value)
    {
        daysUntilDueDate = value;
        return RebuildAndPersistAsync();
    }

    private Task HandleInvoiceTextChanged(string value)
    {
        invoiceText = value;
        return RebuildAndPersistAsync();
    }

    private Task HandleOurReferenceChanged(string value)
    {
        ourReference = value;
        return RebuildAndPersistAsync();
    }

    private Task HandleOrderPrefixChanged(string value)
    {
        orderReferencePrefix = value;
        return RebuildAndPersistAsync();
    }

    private async Task HandleSavedEventSelectedAsync(string projectId)
    {
        minSpeidingProjectId = projectId;
        var found = mappings.SavedEvents.FirstOrDefault(e =>
            string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            eventNameToSave = found.Name;
            rememberEvent = true;
            if (!string.IsNullOrWhiteSpace(found.ApiKey))
            {
                minSpeidingApiKey = found.ApiKey;
            }

            minSpeidingQuestionsApiKey = found.QuestionsApiKey ?? string.Empty;
            saveApiKeyLocally = !string.IsNullOrWhiteSpace(found.ApiKey)
                || !string.IsNullOrWhiteSpace(found.QuestionsApiKey);
        }

        if (eventData is null && !string.IsNullOrWhiteSpace(projectId) && !projectEditedBeforeLoad)
        {
            // Picking another event starts from the defaults, so choices from a previously
            // picked event never carry over.
            ResetPerEventDefaults(eventTitle: null);
            var saved = await ConfigStore.LoadEventDraftStateAsync(projectId);
            if (saved is not null)
            {
                ApplySavedInvoiceFields(saved);
                lastDraftSavedAtLocal = saved.SavedAtUtc.ToLocalTime();
            }
            else
            {
                lastDraftSavedAtLocal = null;
            }
        }
    }

    private async Task FetchFromMinSpeidingAsync()
    {
        isLoadingEvent = true;
        bannerMessage = null;
        var token = ResetEventToken();

        try
        {
            var loaded = await MinSpeidingClient.GetEventParticipantsAsync(
                minSpeidingProjectId,
                minSpeidingApiKey,
                minSpeidingQuestionsApiKey,
                settings.MinSpeidingBaseUrl,
                token);

            if (!string.IsNullOrWhiteSpace(eventNameToSave))
            {
                loaded = loaded with { EventTitle = eventNameToSave.Trim() };
            }

            if (rememberEvent)
            {
                await ConfigStore.SaveEventAsync(new SavedMinSpeidingEvent(
                    Name: string.IsNullOrWhiteSpace(eventNameToSave) ? loaded.EventTitle : eventNameToSave,
                    ProjectId: minSpeidingProjectId,
                    ApiKey: saveApiKeyLocally ? minSpeidingApiKey : null,
                    QuestionsApiKey: saveApiKeyLocally ? minSpeidingQuestionsApiKey : null), token);
                mappings = await ConfigStore.LoadMappingsAsync(token);
            }

            var restored = await ApplyLoadedEventAsync(loaded, loadSavedDraft: true, token);
            SetBanner(
                restored
                    ? $"Hentet {loaded.Participants.Count} deltakere fra Min speiding og gjenopprettet lagrede lokale valg."
                    : $"Hentet {loaded.Participants.Count} deltakere fra Min speiding.",
                isError: false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (MinSpeidingApiException ex)
        {
            Logger.LogWarning(ex, "Failed to load Min speiding project {ProjectId}", minSpeidingProjectId);
            SetBanner(ex.UserMessage, isError: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error loading Min speiding project {ProjectId}", minSpeidingProjectId);
            SetBanner("Kunne ikke hente deltakere fra Min speiding. Sjekk internettforbindelse og innstillinger.", isError: true);
        }
        finally
        {
            isLoadingEvent = false;
        }
    }

    private async Task LoadDemoEventAsync()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        if (customers.Count == 0)
        {
            customers =
            [
                new FikenContact(1001, "Molde speidergruppe", "kasserer@molde.example.no", null, 1, null, true, false, false, 14, null),
                new FikenContact(1002, "1. Kristiansund speidergruppe", "kasserer@1ksund.example.no", null, 2, null, true, false, false, 14, null),
                new FikenContact(1003, "Aukra", "aukra@example.no", null, 3, null, true, false, false, 14, null)
            ];
        }

        var restored = await ApplyLoadedEventAsync(demo, loadSavedDraft: true);
        SetBanner(
            restored
                ? "Lastet inn demodata og gjenopprettet lagrede lokale valg."
                : "Lastet inn demodata for Romsdal og Nordmøre krets.",
            isError: false);
    }

    private async Task<bool> ApplyLoadedEventAsync(
        MinSpeidingEventLoadResult loaded,
        bool loadSavedDraft = true,
        CancellationToken ct = default)
    {
        var preSelectedProjectId = selectedProjectId;
        var keepPreSelectedProject = projectEditedBeforeLoad;

        // Switching to a different event: start from the defaults so the previous event's
        // project, accounts, texts and filters are not reused (and saved) for this one.
        if (eventData is not null
            && !string.Equals(eventData.ProjectId, loaded.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            ResetPerEventDefaults(loaded.EventTitle);
        }

        eventData = loaded;
        categoryPrices.Clear();
        manualCategoryPrices.Clear();
        categoryLabels.Clear();
        excludedCategoryKeys.Clear();
        participantOverrides.Clear();
        manualRecipientContactIds.Clear();
        creationResults.Clear();
        lastDraftSavedAtLocal = null;

        // Categories without a detected price stay unset, so they are shown and warned about as
        // missing a price instead of being treated as free.
        foreach (var cat in loaded.FeeCategories)
        {
            if (cat.SuggestedPriceNok is { } suggestedPrice)
            {
                categoryPrices[cat.Key] = suggestedPrice;
            }
        }

        orderReferencePrefix = string.Empty;
        var restored = false;
        SavedEventDraftState? saved = null;

        if (loadSavedDraft)
        {
            saved = await ConfigStore.LoadEventDraftStateAsync(loaded.ProjectId, ct);
            if (saved is not null)
            {
                var companyMatches = ApplySavedInvoiceFields(saved);
                if (keepPreSelectedProject)
                {
                    selectedProjectId = preSelectedProjectId;
                }

                billingMode = saved.BillingMode;
                groupLineMode = saved.GroupLineMode;
                filters = saved.Filters;

                var suggestedByKey = loaded.FeeCategories
                    .ToDictionary(c => c.Key, c => c.SuggestedPriceNok, StringComparer.OrdinalIgnoreCase);

                // Older files stored every category price, detected or typed. For those, a category
                // with a detected price uses the fresh detection, since the stored value may be stale.
                var isLegacyPriceFormat = saved.FormatVersion < SavedEventDraftState.CurrentFormatVersion;

                foreach (var (key, value) in saved.CategoryPricesNok)
                {
                    if (suggestedByKey.TryGetValue(key, out var suggested)
                        && suggested.HasValue
                        && (isLegacyPriceFormat || suggested.Value == value))
                    {
                        continue;
                    }

                    categoryPrices[key] = value;
                    manualCategoryPrices[key] = value;
                }

                var originalLabelByKey = loaded.FeeCategories
                    .ToDictionary(c => c.Key, c => c.Label.Trim(), StringComparer.OrdinalIgnoreCase);

                foreach (var (key, label) in saved.CategoryLabels)
                {
                    var trimmed = label.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }

                    if (originalLabelByKey.TryGetValue(key, out var orig)
                        && string.Equals(trimmed, orig, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    categoryLabels[key] = trimmed;
                }

                foreach (var key in saved.ExcludedCategoryKeys)
                {
                    excludedCategoryKeys.Add(key);
                }

                foreach (var (key, ov) in saved.ParticipantOverrides)
                {
                    participantOverrides[key] = ov;
                }

                // Customer IDs belong to the Fiken company they were chosen in.
                if (companyMatches)
                {
                    foreach (var (key, contactId) in saved.ManualRecipientContactIds)
                    {
                        manualRecipientContactIds[key] = contactId;
                    }
                }

                lastDraftSavedAtLocal = saved.SavedAtUtc.ToLocalTime();
                restored = true;
            }
        }

        projectEditedBeforeLoad = false;

        if (!restored && string.IsNullOrWhiteSpace(invoiceText))
        {
            invoiceText = $"Deltakeravgift – {loaded.EventTitle}";
        }

        RebuildPlan();

        if (loadSavedDraft && keepPreSelectedProject && saved?.FikenProjectId != selectedProjectId)
        {
            await PersistCurrentDraftStateAsync();
        }

        return restored;
    }

    private async Task ResetSavedEventChoicesAsync()
    {
        if (eventData is null)
        {
            return;
        }

        await ConfigStore.DeleteEventDraftStateAsync(eventData.ProjectId);
        ResetPerEventDefaults(eventData.EventTitle);

        await ApplyLoadedEventAsync(eventData, loadSavedDraft: false);
        SetBanner("Nullstilte lagrede valg for dette arrangementet.", isError: false);
    }

    /// <summary>
    /// Resets every per-event choice to the defaults from Settings.
    /// </summary>
    private void ResetPerEventDefaults(string? eventTitle)
    {
        selectedProjectId = null;
        projectEditedBeforeLoad = false;
        incomeAccount = settings.DefaultIncomeAccount;
        bankAccountNumber = settings.DefaultBankAccountNumber;
        issueDate = DateOnly.FromDateTime(DateTime.Today);
        daysUntilDueDate = settings.DefaultDaysUntilDueDate;
        ourReference = settings.DefaultOurReference;
        orderReferencePrefix = string.Empty;
        invoiceText = eventTitle is null ? string.Empty : $"Deltakeravgift – {eventTitle}";
        billingMode = BillingMode.PerScoutGroup;
        groupLineMode = GroupLineMode.OneLinePerParticipant;
        filters = new ParticipantFilterOptions();
    }

    /// <summary>
    /// Applies the saved invoice fields. Fiken-specific choices (project, income account, bank account)
    /// are only restored when they were saved for the current Fiken company and still exist there.
    /// Returns whether the saved state belongs to the current company.
    /// </summary>
    private bool ApplySavedInvoiceFields(SavedEventDraftState saved)
    {
        var companyMatches = string.IsNullOrWhiteSpace(saved.FikenCompanySlug)
            || string.Equals(saved.FikenCompanySlug, selectedCompanySlug, StringComparison.OrdinalIgnoreCase);

        if (companyMatches)
        {
            selectedProjectId = saved.FikenProjectId is { } savedProjectId
                && (projects.Count == 0 || projects.Any(p => p.ProjectId == savedProjectId))
                    ? savedProjectId
                    : null;

            if (!string.IsNullOrWhiteSpace(saved.IncomeAccount)
                && (incomeAccounts.Count == 0 || incomeAccounts.Any(a => a.Code == saved.IncomeAccount)))
            {
                incomeAccount = saved.IncomeAccount;
            }

            if (!string.IsNullOrWhiteSpace(saved.BankAccountNumber)
                && (bankAccounts.Count == 0 || bankAccounts.Any(b => b.BankAccountNumber == saved.BankAccountNumber)))
            {
                bankAccountNumber = saved.BankAccountNumber;
            }
        }
        else
        {
            selectedProjectId = null;
        }

        // An issue date in the past is almost always stale: keep today's date instead.
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (saved.IssueDate is { } savedIssueDate && savedIssueDate >= today)
        {
            issueDate = savedIssueDate;
        }

        if (saved.DaysUntilDueDate.HasValue)
        {
            daysUntilDueDate = saved.DaysUntilDueDate.Value;
        }

        invoiceText = saved.InvoiceText ?? string.Empty;
        ourReference = saved.OurReference ?? string.Empty;
        orderReferencePrefix = saved.OrderReferencePrefix ?? string.Empty;
        return companyMatches;
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private Task HandleBillingModeChanged(BillingMode mode)
    {
        billingMode = mode;
        return RebuildAndPersistAsync();
    }

    private Task HandleGroupLineModeChanged(GroupLineMode mode)
    {
        groupLineMode = mode;
        return RebuildAndPersistAsync();
    }

    private Task HandleFiltersChanged(ParticipantFilterOptions newFilters)
    {
        filters = newFilters;
        return RebuildAndPersistAsync();
    }

    private Task HandleCategoryPriceChanged((string CategoryKey, decimal PriceNok) change)
    {
        categoryPrices[change.CategoryKey] = change.PriceNok;

        var suggestedPrice = eventData?.FeeCategories
            .FirstOrDefault(c => string.Equals(c.Key, change.CategoryKey, StringComparison.OrdinalIgnoreCase))
            ?.SuggestedPriceNok;

        if (suggestedPrice.HasValue && suggestedPrice.Value == change.PriceNok)
        {
            manualCategoryPrices.Remove(change.CategoryKey);
        }
        else
        {
            manualCategoryPrices[change.CategoryKey] = change.PriceNok;
        }

        return RebuildAndPersistAsync();
    }

    private Task HandleCategoryLabelChanged((string CategoryKey, string? Label) change)
    {
        var trimmed = change.Label?.Trim();
        var originalLabel = eventData?.FeeCategories
            .FirstOrDefault(c => string.Equals(c.Key, change.CategoryKey, StringComparison.OrdinalIgnoreCase))
            ?.Label.Trim();

        if (string.IsNullOrWhiteSpace(trimmed)
            || (!string.IsNullOrWhiteSpace(originalLabel)
                && string.Equals(trimmed, originalLabel, StringComparison.Ordinal)))
        {
            categoryLabels.Remove(change.CategoryKey);
        }
        else
        {
            categoryLabels[change.CategoryKey] = trimmed;
        }

        return RebuildAndPersistAsync();
    }

    private Task HandleCategoryIncludeChanged((string CategoryKey, bool Included) change)
    {
        if (change.Included)
        {
            excludedCategoryKeys.Remove(change.CategoryKey);
        }
        else
        {
            excludedCategoryKeys.Add(change.CategoryKey);
        }

        return RebuildAndPersistAsync();
    }

    private Task HandleParticipantIncludeChanged((string ParticipantKey, bool? Included) change)
    {
        participantOverrides.TryGetValue(change.ParticipantKey, out var existing);
        participantOverrides[change.ParticipantKey] = (existing ?? new ParticipantOverride()) with
        {
            Included = change.Included
        };
        return RebuildAndPersistAsync();
    }

    private Task HandleParticipantCoveredByDistrictChanged((string ParticipantKey, bool CoveredByDistrict) change)
    {
        participantOverrides.TryGetValue(change.ParticipantKey, out var existing);
        participantOverrides[change.ParticipantKey] = (existing ?? new ParticipantOverride()) with
        {
            CoveredByDistrict = change.CoveredByDistrict
        };
        return RebuildAndPersistAsync();
    }

    private Task HandleParticipantPriceChanged((string ParticipantKey, decimal? CustomPriceNok) change)
    {
        participantOverrides.TryGetValue(change.ParticipantKey, out var existing);
        participantOverrides[change.ParticipantKey] = (existing ?? new ParticipantOverride()) with
        {
            CustomPriceNok = change.CustomPriceNok
        };
        return RebuildAndPersistAsync();
    }

    private string? SelectedProjectLabel
    {
        get
        {
            if (!selectedProjectId.HasValue)
            {
                return null;
            }

            var match = projects.FirstOrDefault(p => p.ProjectId == selectedProjectId.Value);
            if (match is null)
            {
                return $"Prosjekt #{selectedProjectId.Value}";
            }

            return string.IsNullOrWhiteSpace(match.Number)
                ? match.Name
                : $"{match.Number} – {match.Name}";
        }
    }

    private IReadOnlyList<string> KnownGroupNames =>
        mappings.GroupNameToContactId.Keys
            .OrderBy(k => k, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ToList();

    private Task HandleParticipantGroupChanged((string ParticipantKey, string? CustomGroupName) change)
    {
        var defaultGroup = eventData?.Participants
            .FirstOrDefault(p => string.Equals(p.ParticipantKey, change.ParticipantKey, StringComparison.OrdinalIgnoreCase))
            ?.GroupName;

        var normalized = string.IsNullOrWhiteSpace(change.CustomGroupName)
            || (!string.IsNullOrWhiteSpace(defaultGroup)
                && string.Equals(change.CustomGroupName.Trim(), defaultGroup, StringComparison.OrdinalIgnoreCase))
            ? null
            : change.CustomGroupName.Trim();

        participantOverrides.TryGetValue(change.ParticipantKey, out var existing);
        participantOverrides[change.ParticipantKey] = (existing ?? new ParticipantOverride()) with
        {
            CustomGroupName = normalized
        };
        return RebuildAndPersistAsync();
    }

    private Task HandleManualCustomerSelected((string DraftKey, long? ContactId) selection)
    {
        if (selection.ContactId.HasValue)
        {
            manualRecipientContactIds[selection.DraftKey] = selection.ContactId.Value;
        }
        else
        {
            manualRecipientContactIds.Remove(selection.DraftKey);
        }

        return RebuildAndPersistAsync();
    }

    private async Task HandleRememberGroupMappingAsync((string RecipientName, long ContactId) args)
    {
        await ConfigStore.SaveGroupMappingAsync(args.RecipientName, args.ContactId);
        mappings = await ConfigStore.LoadMappingsAsync();
        SetBanner($"Lagret kobling for «{args.RecipientName}» til fremtidige arrangementer.", isError: false);
        RebuildPlan();
    }

    private async Task RebuildAndPersistAsync()
    {
        RebuildPlan();
        await PersistCurrentDraftStateAsync();
    }

    private async Task PersistCurrentDraftStateAsync()
    {
        if (eventData is null)
        {
            return;
        }

        try
        {
            var draftState = new SavedEventDraftState
            {
                ProjectId = eventData.ProjectId,
                SavedAtUtc = DateTimeOffset.UtcNow,
                FormatVersion = SavedEventDraftState.CurrentFormatVersion,
                FikenCompanySlug = NullIfEmpty(selectedCompanySlug),
                FikenProjectId = selectedProjectId,
                IncomeAccount = incomeAccount,
                BankAccountNumber = bankAccountNumber,
                IssueDate = issueDate,
                DaysUntilDueDate = daysUntilDueDate,
                InvoiceText = invoiceText,
                OurReference = ourReference,
                OrderReferencePrefix = orderReferencePrefix,
                BillingMode = billingMode,
                GroupLineMode = groupLineMode,
                Filters = filters,
                CategoryPricesNok = new Dictionary<string, decimal>(manualCategoryPrices, StringComparer.OrdinalIgnoreCase),
                CategoryLabels = new Dictionary<string, string>(categoryLabels, StringComparer.OrdinalIgnoreCase),
                ExcludedCategoryKeys = excludedCategoryKeys.ToList(),
                ParticipantOverrides = new Dictionary<string, ParticipantOverride>(participantOverrides, StringComparer.OrdinalIgnoreCase),
                ManualRecipientContactIds = new Dictionary<string, long>(manualRecipientContactIds, StringComparer.OrdinalIgnoreCase)
            };

            await ConfigStore.SaveEventDraftStateAsync(draftState);
            lastDraftSavedAtLocal = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save local draft state for project {ProjectId}", eventData.ProjectId);
        }
    }

    private async Task HandleCreateFikenCustomerAsync((string DraftKey, string Name, string? Email) args)
    {
        if (!fikenTokenConfigured || string.IsNullOrWhiteSpace(selectedCompanySlug))
        {
            SetBanner("Velg Fiken-foretak og legg inn API-nøkkel før du oppretter kunder i Fiken.", isError: true);
            return;
        }

        isSubmittingDrafts = true;
        var token = ResetSubmitToken();
        var companySlug = selectedCompanySlug;

        try
        {
            var created = await FikenClient.CreateCustomerAsync(
                companySlug,
                new FikenCreateContactRequest(args.Name, args.Email, Customer: true),
                token);

            customers = (await FikenClient.GetCustomersAsync(companySlug, token)).ToList();
            if (created.Id.HasValue)
            {
                manualRecipientContactIds[args.DraftKey] = created.Id.Value;
            }

            SetBanner($"Opprettet kunden «{args.Name}» i Fiken.", isError: false);
            await RebuildAndPersistAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (FikenApiException ex)
        {
            Logger.LogWarning(ex, "Failed to create Fiken customer {Name}", args.Name);
            SetBanner(ex.UserMessage, isError: true);
        }
        finally
        {
            isSubmittingDrafts = false;
        }
    }

    private async Task CreateFikenDraftsAsync()
    {
        if (invoicePlan is null)
        {
            return;
        }

        if (!fikenTokenConfigured || string.IsNullOrWhiteSpace(selectedCompanySlug))
        {
            SetBanner("Fiken API-nøkkel og foretak må være satt før utkast kan opprettes.", isError: true);
            return;
        }

        var readyDrafts = invoicePlan.Drafts.Where(d => d.IsReadyToCreate).ToList();
        if (readyDrafts.Count == 0)
        {
            SetBanner("Ingen fakturautkast er klare for opprettelse.", isError: true);
            return;
        }

        isSubmittingDrafts = true;
        creationResults = [];
        bannerMessage = null;
        var token = ResetSubmitToken();
        var options = BuildPlanOptions();
        // Capture the company once: every draft in this run must go to the company the plan was built for.
        var companySlug = selectedCompanySlug;

        try
        {
            foreach (var draft in readyDrafts)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var request = InvoicePlanBuilder.ToFikenRequest(draft, options);
                    var created = await FikenClient.CreateInvoiceDraftAsync(companySlug, request, token);
                    creationResults.Add(new DraftCreationItemResult(
                        draft.DraftKey,
                        draft.RecipientName,
                        Success: true,
                        CreatedDraftId: created.Id,
                        ErrorMessage: null));
                }
                catch (FikenApiException ex)
                {
                    Logger.LogWarning(ex, "Failed to create draft for {Recipient}", draft.RecipientName);
                    creationResults.Add(new DraftCreationItemResult(
                        draft.DraftKey,
                        draft.RecipientName,
                        Success: false,
                        CreatedDraftId: null,
                        ErrorMessage: ex.UserMessage));
                }
            }

            existingDrafts = (await FikenClient.GetInvoiceDraftsAsync(companySlug, token)).ToList();

            var okCount = creationResults.Count(r => r.Success);
            var errCount = creationResults.Count - okCount;
            SetBanner(
                errCount == 0
                    ? $"Opprettet {okCount} fakturautkast i Fiken. Du kan nå se over og sende dem fra Fiken."
                    : $"Opprettet {okCount} fakturautkast, men {errCount} feilet. Se detaljer nedenfor.",
                isError: errCount > 0);

            RebuildPlan();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            isSubmittingDrafts = false;
        }
    }

    private InvoiceDraftPlanOptions BuildPlanOptions() => new(
        BillingMode: billingMode,
        GroupLineMode: groupLineMode,
        Filters: filters,
        CategoryPricesNok: categoryPrices,
        ParticipantOverrides: participantOverrides,
        ManualRecipientContactIds: manualRecipientContactIds,
        IncomeAccount: incomeAccount,
        IssueDate: issueDate,
        DaysUntilDueDate: daysUntilDueDate,
        FikenProjectId: selectedProjectId,
        BankAccountNumber: bankAccountNumber,
        InvoiceText: invoiceText,
        OurReference: ourReference,
        OrderReferencePrefix: orderReferencePrefix,
        ExcludedCategoryKeys: excludedCategoryKeys,
        CategoryLabels: categoryLabels);

    private void RebuildPlan()
    {
        if (eventData is null)
        {
            invoicePlan = null;
            return;
        }

        invoicePlan = InvoicePlanBuilder.Build(
            eventData,
            BuildPlanOptions(),
            customers,
            mappings.GroupNameToContactId,
            existingDrafts);
    }

    private void SetBanner(string message, bool isError)
    {
        bannerMessage = message;
        bannerIsError = isError;
    }

    private CancellationToken ResetFikenToken() => ResetToken(ref fikenCts);

    private CancellationToken ResetEventToken() => ResetToken(ref eventCts);

    private CancellationToken ResetSubmitToken() => ResetToken(ref submitCts);

    private static CancellationToken ResetToken(ref CancellationTokenSource? slot)
    {
        slot?.Cancel();
        slot?.Dispose();
        slot = new CancellationTokenSource();
        return slot.Token;
    }

    private static async Task DisposeCtsAsync(CancellationTokenSource? source)
    {
        if (source is not null)
        {
            await source.CancelAsync();
            source.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeCtsAsync(fikenCts);
        await DisposeCtsAsync(eventCts);
        await DisposeCtsAsync(submitCts);
    }
}
