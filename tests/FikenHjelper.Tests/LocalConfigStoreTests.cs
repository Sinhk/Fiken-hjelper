using FikenHjelper.Core.Config;
using FikenHjelper.Core.Invoicing;

namespace FikenHjelper.Tests;

public sealed class LocalConfigStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "fiken-hjelper-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadSettings_RoundTripsAndFallsBackToDefaultsWhenEmpty()
    {
        var store = new LocalConfigStore(
            new AppSettingsDefaults(
                FikenApiToken: "secret-from-user-secrets",
                FikenCompanySlug: "krets-slug"),
            _tempDir);

        var initial = await store.LoadSettingsAsync();
        Assert.Equal("secret-from-user-secrets", initial.FikenApiToken);
        Assert.Equal("krets-slug", initial.FikenCompanySlug);

        await store.SaveSettingsAsync(new AppSettings
        {
            FikenApiToken = "",
            FikenCompanySlug = "custom-slug",
            DefaultIncomeAccount = "3210",
            DefaultDaysUntilDueDate = 21,
            DefaultBankAccountNumber = "12345678903",
            DefaultOurReference = "Kasserer"
        });

        var loaded = await store.LoadSettingsAsync();
        Assert.Equal("secret-from-user-secrets", loaded.FikenApiToken);
        Assert.Equal("custom-slug", loaded.FikenCompanySlug);
        Assert.Equal("3210", loaded.DefaultIncomeAccount);
        Assert.Equal(21, loaded.DefaultDaysUntilDueDate);
    }

    [Fact]
    public async Task SaveGroupMappingAndSavedEvent_PersistsCaseInsensitiveMappings()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);

        await store.SaveGroupMappingAsync("Molde speidergruppe", 5001);
        await store.SaveEventAsync(new SavedMinSpeidingEvent("Kretsleir", "444", "abc", "q-key"));

        var mappings = await store.LoadMappingsAsync();
        Assert.True(mappings.GroupNameToContactId.TryGetValue("MOLDE SPEIDERGRUPPE", out var id));
        Assert.Equal(5001, id);
        Assert.Single(mappings.SavedEvents);
        Assert.Equal("444", mappings.SavedEvents[0].ProjectId);
        Assert.Equal("abc", mappings.SavedEvents[0].ApiKey);
        Assert.Equal("q-key", mappings.SavedEvents[0].QuestionsApiKey);

        await store.RemoveGroupMappingAsync("molde speidergruppe");
        var afterRemove = await store.LoadMappingsAsync();
        Assert.Empty(afterRemove.GroupNameToContactId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLoadAndDeleteEventDraftState_RoundTripsOverridesAndFilters()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);

        Assert.Null(await store.LoadEventDraftStateAsync("12345"));

        var state = new SavedEventDraftState
        {
            ProjectId = "12345",
            FikenProjectId = 777,
            IncomeAccount = "3210",
            BankAccountNumber = "12345678903",
            IssueDate = new DateOnly(2026, 11, 24),
            DaysUntilDueDate = 21,
            InvoiceText = "Kretsleir 2026",
            OurReference = "Kasserer",
            OrderReferencePrefix = "KL26",
            BillingMode = BillingMode.PerParticipant,
            GroupLineMode = GroupLineMode.SummaryPerFeeCategory,
            Filters = new ParticipantFilterOptions(
                ExcludeCancelled: false,
                RequireConfirmed: true,
                RequireAttended: true,
                ExcludeZeroPrice: true),
            CategoryPricesNok = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["fee:101"] = 490.50m
            },
            CategoryLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fee:101"] = "  Deltakeravgift speider  "
            },
            ExcludedCategoryKeys = ["fee:103"],
            ParticipantOverrides = new Dictionary<string, ParticipantOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["90003"] = new ParticipantOverride(
                    Included: true,
                    CustomPriceNok: 199.75m,
                    CoveredByDistrict: true,
                    CustomGroupName: "Aukra speidergruppe")
            },
            ManualRecipientContactIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["group:Molde speidergruppe"] = 5001
            }
        };

        await store.SaveEventDraftStateAsync(state);

        var loaded = await store.LoadEventDraftStateAsync("12345");
        Assert.NotNull(loaded);
        Assert.Equal("12345", loaded.ProjectId);
        Assert.Equal(777, loaded.FikenProjectId);
        Assert.Equal("3210", loaded.IncomeAccount);
        Assert.Equal("12345678903", loaded.BankAccountNumber);
        Assert.Equal(new DateOnly(2026, 11, 24), loaded.IssueDate);
        Assert.Equal(21, loaded.DaysUntilDueDate);
        Assert.Equal("Kretsleir 2026", loaded.InvoiceText);
        Assert.Equal("Kasserer", loaded.OurReference);
        Assert.Equal("KL26", loaded.OrderReferencePrefix);
        Assert.Equal(BillingMode.PerParticipant, loaded.BillingMode);
        Assert.Equal(GroupLineMode.SummaryPerFeeCategory, loaded.GroupLineMode);
        Assert.False(loaded.Filters.ExcludeCancelled);
        Assert.True(loaded.Filters.RequireConfirmed);
        Assert.True(loaded.Filters.RequireAttended);
        Assert.True(loaded.Filters.ExcludeZeroPrice);
        Assert.Equal(490.50m, loaded.CategoryPricesNok["FEE:101"]);
        Assert.Equal("Deltakeravgift speider", loaded.CategoryLabels["FEE:101"]);
        Assert.Single(loaded.ExcludedCategoryKeys, "fee:103");
        Assert.True(loaded.ParticipantOverrides.TryGetValue("90003", out var ov));
        Assert.Equal(true, ov!.Included);
        Assert.Equal(199.75m, ov.CustomPriceNok);
        Assert.True(ov.CoveredByDistrict);
        Assert.Equal("Aukra speidergruppe", ov.CustomGroupName);
        Assert.Equal(5001, loaded.ManualRecipientContactIds["GROUP:MOLDE SPEIDERGRUPPE"]);

        await store.DeleteEventDraftStateAsync("12345");
        Assert.Null(await store.LoadEventDraftStateAsync("12345"));
    }
}
