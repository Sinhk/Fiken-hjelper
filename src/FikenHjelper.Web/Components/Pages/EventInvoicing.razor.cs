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
    private HashSet<string> existingDraftOrderReferences = new(StringComparer.OrdinalIgnoreCase);

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
    private string eventNameToSave = string.Empty;
    private bool rememberEvent;
    private bool saveApiKeyLocally;

    private BillingMode billingMode = BillingMode.PerScoutGroup;
    private GroupLineMode groupLineMode = GroupLineMode.OneLinePerParticipant;
    private ParticipantFilterOptions filters = new();

    private MinSpeidingEventLoadResult? eventData;
    private readonly Dictionary<string, decimal> categoryPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParticipantOverride> participantOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> manualRecipientContactIds = new(StringComparer.OrdinalIgnoreCase);
    private InvoicePlanResult? invoicePlan;
    private List<DraftCreationItemResult> creationResults = [];

    private CancellationTokenSource? cts;
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
        var token = ResetCancellationToken();

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

        var existingDrafts = await FikenClient.GetInvoiceDraftsAsync(companySlug, token);
        existingDraftOrderReferences = existingDrafts
            .Where(d => !string.IsNullOrWhiteSpace(d.OrderReference))
            .Select(d => d.OrderReference!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        selectedCompanySlug = newSlug;
        selectedProjectId = null;
        customers = [];
        projects = [];
        incomeAccounts = [];
        bankAccounts = [];
        existingDraftOrderReferences.Clear();

        if (!string.IsNullOrWhiteSpace(selectedCompanySlug) && fikenTokenConfigured)
        {
            isLoadingFiken = true;
            var token = ResetCancellationToken();
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

    private void HandleProjectChanged(long? projectId)
    {
        selectedProjectId = projectId;
        RebuildPlan();
    }

    private void HandleIncomeAccountChanged(string value)
    {
        incomeAccount = value;
        RebuildPlan();
    }

    private void HandleBankAccountChanged(string value)
    {
        bankAccountNumber = value;
        RebuildPlan();
    }

    private void HandleIssueDateChanged(DateOnly value)
    {
        issueDate = value;
        RebuildPlan();
    }

    private void HandleDueDaysChanged(int value)
    {
        daysUntilDueDate = value;
        RebuildPlan();
    }

    private void HandleInvoiceTextChanged(string value)
    {
        invoiceText = value;
        RebuildPlan();
    }

    private void HandleOurReferenceChanged(string value)
    {
        ourReference = value;
        RebuildPlan();
    }

    private void HandleOrderPrefixChanged(string value)
    {
        orderReferencePrefix = value;
        RebuildPlan();
    }

    private void HandleSavedEventSelected(string projectId)
    {
        minSpeidingProjectId = projectId;
        var found = mappings.SavedEvents.FirstOrDefault(e =>
            string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            eventNameToSave = found.Name;
            if (!string.IsNullOrWhiteSpace(found.ApiKey))
            {
                minSpeidingApiKey = found.ApiKey;
            }
        }
    }

    private async Task FetchFromMinSpeidingAsync()
    {
        isLoadingEvent = true;
        bannerMessage = null;
        var token = ResetCancellationToken();

        try
        {
            var loaded = await MinSpeidingClient.GetEventParticipantsAsync(
                minSpeidingProjectId,
                minSpeidingApiKey,
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
                    ApiKey: saveApiKeyLocally ? minSpeidingApiKey : null), token);
                mappings = await ConfigStore.LoadMappingsAsync(token);
            }

            ApplyLoadedEvent(loaded);
            SetBanner($"Hentet {loaded.Participants.Count} deltakere fra Min speiding.", isError: false);
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

    private void LoadDemoEvent()
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

        ApplyLoadedEvent(demo);
        SetBanner("Lastet inn demodata for Romsdal og Nordmøre krets.", isError: false);
    }

    private void ApplyLoadedEvent(MinSpeidingEventLoadResult loaded)
    {
        eventData = loaded;
        categoryPrices.Clear();
        participantOverrides.Clear();
        manualRecipientContactIds.Clear();
        creationResults.Clear();

        foreach (var cat in loaded.FeeCategories)
        {
            categoryPrices[cat.Key] = cat.SuggestedPriceNok ?? 0m;
        }

        orderReferencePrefix = $"MS-{loaded.ProjectId}";
        if (string.IsNullOrWhiteSpace(invoiceText))
        {
            invoiceText = $"Deltakeravgift – {loaded.EventTitle}";
        }

        RebuildPlan();
    }

    private void HandleBillingModeChanged(BillingMode mode)
    {
        billingMode = mode;
        RebuildPlan();
    }

    private void HandleGroupLineModeChanged(GroupLineMode mode)
    {
        groupLineMode = mode;
        RebuildPlan();
    }

    private void HandleFiltersChanged(ParticipantFilterOptions newFilters)
    {
        filters = newFilters;
        RebuildPlan();
    }

    private void HandleCategoryPriceChanged((string CategoryKey, decimal PriceNok) change)
    {
        categoryPrices[change.CategoryKey] = change.PriceNok;
        RebuildPlan();
    }

    private void HandleParticipantIncludeChanged((string ParticipantKey, bool? Included) change)
    {
        participantOverrides.TryGetValue(change.ParticipantKey, out var existing);
        participantOverrides[change.ParticipantKey] = (existing ?? new ParticipantOverride()) with
        {
            Included = change.Included
        };
        RebuildPlan();
    }

    private void HandleParticipantPriceChanged((string ParticipantKey, decimal? CustomPriceNok) change)
    {
        participantOverrides.TryGetValue(change.ParticipantKey, out var existing);
        participantOverrides[change.ParticipantKey] = (existing ?? new ParticipantOverride()) with
        {
            CustomPriceNok = change.CustomPriceNok
        };
        RebuildPlan();
    }

    private void HandleManualCustomerSelected((string DraftKey, long? ContactId) selection)
    {
        if (selection.ContactId.HasValue)
        {
            manualRecipientContactIds[selection.DraftKey] = selection.ContactId.Value;
        }
        else
        {
            manualRecipientContactIds.Remove(selection.DraftKey);
        }

        RebuildPlan();
    }

    private async Task HandleRememberGroupMappingAsync((string RecipientName, long ContactId) args)
    {
        await ConfigStore.SaveGroupMappingAsync(args.RecipientName, args.ContactId);
        mappings = await ConfigStore.LoadMappingsAsync();
        SetBanner($"Lagret kobling for «{args.RecipientName}» til fremtidige arrangementer.", isError: false);
        RebuildPlan();
    }

    private async Task HandleCreateFikenCustomerAsync((string DraftKey, string Name, string? Email) args)
    {
        if (!fikenTokenConfigured || string.IsNullOrWhiteSpace(selectedCompanySlug))
        {
            SetBanner("Velg Fiken-foretak og legg inn API-nøkkel før du oppretter kunder i Fiken.", isError: true);
            return;
        }

        isSubmittingDrafts = true;
        var token = ResetCancellationToken();

        try
        {
            var created = await FikenClient.CreateCustomerAsync(
                selectedCompanySlug,
                new FikenCreateContactRequest(args.Name, args.Email, Customer: true),
                token);

            customers = (await FikenClient.GetCustomersAsync(selectedCompanySlug, token)).ToList();
            if (created.Id.HasValue)
            {
                manualRecipientContactIds[args.DraftKey] = created.Id.Value;
            }

            SetBanner($"Opprettet kunden «{args.Name}» i Fiken.", isError: false);
            RebuildPlan();
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
        var token = ResetCancellationToken();
        var options = BuildPlanOptions();

        try
        {
            foreach (var draft in readyDrafts)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var request = InvoicePlanBuilder.ToFikenRequest(draft, options);
                    var created = await FikenClient.CreateInvoiceDraftAsync(selectedCompanySlug, request, token);
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

            var draftsInFiken = await FikenClient.GetInvoiceDraftsAsync(selectedCompanySlug, token);
            existingDraftOrderReferences = draftsInFiken
                .Where(d => !string.IsNullOrWhiteSpace(d.OrderReference))
                .Select(d => d.OrderReference!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        OrderReferencePrefix: orderReferencePrefix);

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
            existingDraftOrderReferences);
    }

    private void SetBanner(string message, bool isError)
    {
        bannerMessage = message;
        bannerIsError = isError;
    }

    private CancellationToken ResetCancellationToken()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts.Token;
    }

    public async ValueTask DisposeAsync()
    {
        if (cts is not null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
    }
}
