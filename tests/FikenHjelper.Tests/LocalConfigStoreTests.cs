using FikenHjelper.Core.Config;

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
}
