using System.ComponentModel.DataAnnotations;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using Microsoft.AspNetCore.Components;

namespace FikenHjelper.Web.Components.Pages;

public sealed partial class Settings : ComponentBase, IAsyncDisposable
{
    [Inject] private ILocalConfigStore ConfigStore { get; set; } = default!;
    [Inject] private IFikenClient FikenClient { get; set; } = default!;
    [Inject] private IFikenOAuthService OAuthService { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILogger<Settings> Logger { get; set; } = default!;
    [Inject] private FikenDataCache FikenCache { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "oauth")]
    public string? OAuthQueryStatus { get; set; }

    [SupplyParameterFromQuery(Name = "oauthError")]
    public string? OAuthQueryError { get; set; }

    private readonly SettingsFormModel formModel = new();
    private MappingsData mappings = new();
    private List<FikenCompany> companies = [];
    private List<FikenAccount> accounts = [];
    private List<FikenBankAccount> bankAccounts = [];

    private CancellationTokenSource? cts;
    private bool showClientSecret;
    private bool showToken;
    private bool hasOAuthTokens;
    private DateTimeOffset? oauthExpiresAtUtc;
    private bool hasAutoTestedAfterOAuth;
    private bool isTestingFiken;
    private bool isSaving;
    private string? statusMessage;
    private bool statusIsError;

    private string CallbackUri => $"{Navigation.BaseUri.TrimEnd('/')}/fiken/oauth/callback";

    protected override async Task OnInitializedAsync()
    {
        var saved = await ConfigStore.LoadSettingsAsync();
        formModel.LoadFrom(saved);
        hasOAuthTokens = saved.HasFikenOAuthTokens;
        oauthExpiresAtUtc = saved.FikenOAuthExpiresAtUtc;
        mappings = await ConfigStore.LoadMappingsAsync();

        ApplyOAuthQueryStatus();

        if (RendererInfo.IsInteractive
            && !hasAutoTestedAfterOAuth
            && string.Equals(OAuthQueryStatus, "connected", StringComparison.OrdinalIgnoreCase))
        {
            hasAutoTestedAfterOAuth = true;
            await TestConnectionAndLoadAsync();
        }
    }

    private void ApplyOAuthQueryStatus()
    {
        if (!string.IsNullOrWhiteSpace(OAuthQueryError))
        {
            statusIsError = true;
            statusMessage = OAuthQueryError switch
            {
                "missing_client_id" => "Lim inn Client ID fra Fiken-appen før du logger inn.",
                "access_denied" => "Innloggingen ble avbrutt i Fiken.",
                "invalid_state" => "Innloggingen utløp eller hadde ugyldig sikkerhetstoken. Prøv igjen.",
                "token_exchange_failed" => "Kunne ikke fullføre token-bytte mot Fiken. Kontroller Client ID, Client Secret og Redirect URI.",
                _ => "Innloggingen mot Fiken feilet. Prøv igjen."
            };
            return;
        }

        if (string.Equals(OAuthQueryStatus, "connected", StringComparison.OrdinalIgnoreCase))
        {
            statusIsError = false;
            statusMessage = "Koblet til Fiken med OAuth2!";
        }
    }

    private void ToggleClientSecretVisibility() => showClientSecret = !showClientSecret;
    private void ToggleTokenVisibility() => showToken = !showToken;

    private async Task SaveAndStartOAuthLoginAsync()
    {
        statusMessage = null;
        statusIsError = false;

        if (string.IsNullOrWhiteSpace(formModel.FikenOAuthClientId))
        {
            statusMessage = "Lim inn Client ID fra Fiken-appen før du logger inn.";
            statusIsError = true;
            return;
        }

        isSaving = true;
        try
        {
            await ConfigStore.SaveSettingsAsync(formModel.ToAppSettings());
            Navigation.NavigateTo("fiken/oauth/login", forceLoad: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save settings before starting OAuth login");
            statusMessage = "Kunne ikke lagre OAuth-oppsett lokalt.";
            statusIsError = true;
            isSaving = false;
        }
    }

    private async Task DisconnectOAuthAsync()
    {
        isSaving = true;
        statusMessage = null;
        statusIsError = false;
        var token = ResetCancellationToken();

        try
        {
            await OAuthService.DisconnectAsync(token);
            FikenCache.Clear();
            var saved = await ConfigStore.LoadSettingsAsync(token);
            hasOAuthTokens = saved.HasFikenOAuthTokens;
            oauthExpiresAtUtc = saved.FikenOAuthExpiresAtUtc;
            if (!saved.IsFikenConfigured)
            {
                companies = [];
                accounts = [];
                bankAccounts = [];
            }

            statusMessage = "Koblet fra Fiken OAuth2.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error while disconnecting OAuth");
            statusMessage = "Kunne ikke koble fra Fiken OAuth2.";
            statusIsError = true;
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task TestConnectionAndLoadAsync()
    {
        statusMessage = null;
        statusIsError = false;
        isTestingFiken = true;
        var token = ResetCancellationToken();

        try
        {
            var tokenOverride = string.IsNullOrWhiteSpace(formModel.FikenApiToken)
                ? null
                : formModel.FikenApiToken;

            var found = await FikenClient.GetCompaniesAsync(tokenOverride, token);
            companies = found.ToList();

            if (string.IsNullOrWhiteSpace(formModel.FikenCompanySlug) && companies.Count > 0)
            {
                formModel.FikenCompanySlug = companies[0].Slug;
            }

            if (!string.IsNullOrWhiteSpace(formModel.FikenCompanySlug))
            {
                await LoadCompanyDetailsUnlockedAsync(formModel.FikenCompanySlug, token);
            }

            var updatedSettings = await ConfigStore.LoadSettingsAsync(token);
            hasOAuthTokens = updatedSettings.HasFikenOAuthTokens;
            oauthExpiresAtUtc = updatedSettings.FikenOAuthExpiresAtUtc;

            statusMessage = companies.Count > 0
                ? $"Tilkobling OK! Fant {companies.Count} foretak i Fiken."
                : "Tilkoblingen virker, men ingen foretak ble funnet på brukeren.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (FikenApiException ex)
        {
            Logger.LogWarning(ex, "Fiken connection test failed");
            statusMessage = ex.UserMessage;
            statusIsError = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error while testing Fiken connection");
            statusMessage = "Kunne ikke kontakte Fiken API. Sjekk nettverkstilkobling og prøv igjen.";
            statusIsError = true;
        }
        finally
        {
            isTestingFiken = false;
        }
    }

    private async Task LoadCompanyDefaultsAsync()
    {
        if (string.IsNullOrWhiteSpace(formModel.FikenCompanySlug))
        {
            return;
        }

        if (!hasOAuthTokens && string.IsNullOrWhiteSpace(formModel.FikenApiToken))
        {
            return;
        }

        var token = ResetCancellationToken();
        try
        {
            await LoadCompanyDetailsUnlockedAsync(formModel.FikenCompanySlug, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (FikenApiException ex)
        {
            Logger.LogWarning(ex, "Could not load accounts for {Company}", formModel.FikenCompanySlug);
            statusMessage = ex.UserMessage;
            statusIsError = true;
        }
    }

    private async Task LoadCompanyDetailsUnlockedAsync(string companySlug, CancellationToken token)
    {
        var tokenOverride = string.IsNullOrWhiteSpace(formModel.FikenApiToken)
            ? null
            : formModel.FikenApiToken;

        accounts = (await FikenClient.GetIncomeAccountsAsync(companySlug, token, tokenOverride)).ToList();
        bankAccounts = (await FikenClient.GetBankAccountsAsync(companySlug, token, tokenOverride)).ToList();
    }

    private async Task SaveSettingsAsync()
    {
        isSaving = true;
        statusMessage = null;
        statusIsError = false;
        try
        {
            await ConfigStore.SaveSettingsAsync(formModel.ToAppSettings());
            FikenCache.Clear();
            statusMessage = "Innstillinger lagret lokalt.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save settings");
            statusMessage = "Kunne ikke lagre innstillinger lokalt.";
            statusIsError = true;
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task RemoveGroupMappingAsync(string groupName)
    {
        await ConfigStore.RemoveGroupMappingAsync(groupName);
        mappings = await ConfigStore.LoadMappingsAsync();
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

    public sealed class SettingsFormModel
    {
        public string FikenOAuthClientId { get; set; } = string.Empty;
        public string FikenOAuthClientSecret { get; set; } = string.Empty;
        public string FikenApiToken { get; set; } = string.Empty;
        public string FikenCompanySlug { get; set; } = string.Empty;

        [Required(ErrorMessage = "Base-URL for Min speiding må fylles ut.")]
        public string MinSpeidingBaseUrl { get; set; } = "https://min.speiding.no/api";

        [Required(ErrorMessage = "Standard inntektskonto må fylles ut.")]
        public string DefaultIncomeAccount { get; set; } = "3200";

        [Range(0, 365, ErrorMessage = "Forfallsfrist må være mellom 0 og 365 dager.")]
        public int DefaultDaysUntilDueDate { get; set; } = 14;

        public string DefaultBankAccountNumber { get; set; } = string.Empty;
        public string DefaultOurReference { get; set; } = string.Empty;

        public void LoadFrom(AppSettings source)
        {
            FikenOAuthClientId = source.FikenOAuthClientId;
            FikenOAuthClientSecret = source.FikenOAuthClientSecret;
            FikenApiToken = source.FikenApiToken;
            FikenCompanySlug = source.FikenCompanySlug;
            MinSpeidingBaseUrl = source.MinSpeidingBaseUrl;
            DefaultIncomeAccount = source.DefaultIncomeAccount;
            DefaultDaysUntilDueDate = source.DefaultDaysUntilDueDate;
            DefaultBankAccountNumber = source.DefaultBankAccountNumber;
            DefaultOurReference = source.DefaultOurReference;
        }

        public AppSettings ToAppSettings() => new()
        {
            FikenOAuthClientId = FikenOAuthClientId,
            FikenOAuthClientSecret = FikenOAuthClientSecret,
            FikenApiToken = FikenApiToken,
            FikenCompanySlug = FikenCompanySlug,
            MinSpeidingBaseUrl = MinSpeidingBaseUrl,
            DefaultIncomeAccount = DefaultIncomeAccount,
            DefaultDaysUntilDueDate = DefaultDaysUntilDueDate,
            DefaultBankAccountNumber = DefaultBankAccountNumber,
            DefaultOurReference = DefaultOurReference
        };
    }
}
