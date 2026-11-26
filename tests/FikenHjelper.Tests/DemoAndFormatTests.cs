using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.MinSpeiding;
using FikenHjelper.Core.Text;

namespace FikenHjelper.Tests;

public sealed class DemoAndFormatTests
{
    private static readonly FikenContact RealCustomer =
        new(900001, "Testgruppe speidergruppe", "kasserer@example.no", null, 1, null, true, false, false, 14, null);

    [Fact]
    public void CustomersForPlan_UsesDemoCustomers_OnlyForDemoDataWithoutRealCustomers()
    {
        Assert.Same(DemoEventFactory.DemoCustomers, DemoEventFactory.CustomersForPlan([], isDemoData: true));
        Assert.Equal([RealCustomer], DemoEventFactory.CustomersForPlan([RealCustomer], isDemoData: true));
    }

    [Fact]
    public void CustomersForPlan_NeverUsesDemoCustomers_ForRealData()
    {
        // Testforetak uten kunder: demodata lastet først, så ekte data. Demokundene skal ikke følge med.
        Assert.Empty(DemoEventFactory.CustomersForPlan([], isDemoData: false));
        Assert.Equal([RealCustomer], DemoEventFactory.CustomersForPlan([RealCustomer], isDemoData: false));
    }

    [Fact]
    public void SafeFormat_WithoutArguments_ReturnsTemplateUnchanged() =>
        Assert.Equal("Side {0}", SafeFormat.Format("Side {0}"));

    [Fact]
    public void SafeFormat_InsertsArguments() =>
        Assert.Equal("Du valgte 3 filer, men maks 2 er tillatt.", SafeFormat.Format("Du valgte {0} filer, men maks {1} er tillatt.", 3, 2));

    [Fact]
    public void SafeFormat_TooFewArguments_ReturnsTemplateInsteadOfThrowing() =>
        Assert.Equal("{0}–{1} av {2}", SafeFormat.Format("{0}–{1} av {2}", 1));

    [Fact]
    public void SafeFormat_NullArguments_ReturnsTemplate() =>
        Assert.Equal("Side {0}", SafeFormat.Format("Side {0}", null));
}
