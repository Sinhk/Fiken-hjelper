using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Invoicing;

namespace FikenHjelper.Tests;

public sealed class CustomerMatcherTests
{
    private static readonly List<FikenContact> SampleCustomers =
    [
        new(101, "Molde speidergruppe", "molde@example.no", null, 1, null, true, false, false, 14, null),
        new(102, "1. Kristiansund speidergruppe", "1ksund@example.no", null, 2, null, true, false, false, 14, null),
        new(103, "2. Kristiansund speidergruppe", "2ksund@example.no", null, 3, null, true, false, false, 14, null),
        new(104, "Aukra", "aukra@example.no", null, 4, null, true, false, false, 14, null),
        new(105, "Inaktiv gruppe", "inaktiv@example.no", null, 5, null, true, false, true, 14, null)
    ];

    [Fact]
    public void Match_SavedMapping_TakesHighestPriority()
    {
        var mappings = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["Aukra speidergruppe"] = 104
        };

        var match = CustomerMatcher.Match("Aukra speidergruppe", null, SampleCustomers, mappings);

        Assert.Equal(CustomerMatchConfidence.SavedMapping, match.Confidence);
        Assert.Equal(104, match.MatchedContactId);
    }

    [Fact]
    public void Match_NormalizedScoutGroupName_MatchesAukraSpeidergruppeToAukra()
    {
        var match = CustomerMatcher.Match(
            "Aukra speidergruppe",
            null,
            SampleCustomers,
            new Dictionary<string, long>());

        Assert.Equal(CustomerMatchConfidence.NormalizedName, match.Confidence);
        Assert.Equal(104, match.MatchedContactId);
    }

    [Fact]
    public void Match_PreservesOrdinalNumbersSo1And2KristiansundNeverCrossMatch()
    {
        var onlySecondExists = SampleCustomers.Where(c => c.ContactId != 102).ToList();

        var match = CustomerMatcher.Match(
            "1. Kristiansund speidergruppe",
            null,
            onlySecondExists,
            new Dictionary<string, long>());

        Assert.NotEqual(103, match.MatchedContactId);
        Assert.Null(match.MatchedContactId);
    }

    [Fact]
    public void Match_ExactEmail_MatchesWhenNameDiffers()
    {
        var match = CustomerMatcher.Match(
            "Ukjent visningsnavn",
            "molde@example.no",
            SampleCustomers,
            new Dictionary<string, long>());

        Assert.Equal(CustomerMatchConfidence.ExactEmail, match.Confidence);
        Assert.Equal(101, match.MatchedContactId);
    }

    [Fact]
    public void Match_InactiveCustomerIsIgnored()
    {
        var match = CustomerMatcher.Match(
            "Inaktiv gruppe",
            "inaktiv@example.no",
            SampleCustomers,
            new Dictionary<string, long>());

        Assert.Null(match.MatchedContactId);
        Assert.Equal(CustomerMatchConfidence.Unmatched, match.Confidence);
    }
}
