using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Grants;

namespace FikenHjelper.Tests;

internal static class GrantTestData
{
    public const long Leir = 900001;
    public const long Kurs = 900002;

    /// <summary>Kostnad: debet på resultatkonto (positivt i øre), motpost 1920.</summary>
    public static FikenJournalEntry Cost(long id, string account, decimal kroner, long? project, string date = "2027-03-01") =>
        new(id, id, "Fri", (int)id, $"Bilag {id}", date,
            [
                new FikenJournalEntryLine((long)(kroner * 100), account, null, project is { } p ? [p] : []),
                new FikenJournalEntryLine((long)(-kroner * 100), "1920", null, [])
            ]);

    /// <summary>Inntekt: kredit på resultatkonto (negativt i øre), motpost 1920.</summary>
    public static FikenJournalEntry Income(long id, string account, decimal kroner, long? project, string description = "Innbetaling", string date = "2027-04-01") =>
        new(id, id, "Fri", (int)id, description, date,
            [
                new FikenJournalEntryLine((long)(-kroner * 100), account, null, project is { } p ? [p] : []),
                new FikenJournalEntryLine((long)(kroner * 100), "1920", null, [])
            ]);

    public static Grant Project(string name, params long[] projects) => new()
    {
        Name = name,
        Grantor = "Eksempel kommune",
        Kind = GrantKind.Project,
        Status = GrantStatus.Granted,
        ProjectIds = [.. projects]
    };
}

public sealed class GrantReportTests
{
    private static GrantReport Build(Grant grant, IReadOnlyList<Grant>? all, IEnumerable<FikenJournalEntry>? entries,
        IReadOnlyList<FikenAccount>? accounts = null) =>
        GrantReportBuilder.Build(grant, all ?? [grant], entries, accounts);

    [Fact]
    public void Used_CountsCostsAsPositiveAndIgnoresIncome()
    {
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);
        grant.GrantedAmount = 50000;
        var report = Build(grant, null,
        [
            GrantTestData.Cost(1, "6800", 1200.50m, GrantTestData.Kurs),
            GrantTestData.Cost(2, "7140", 300m, GrantTestData.Kurs),
            GrantTestData.Income(3, "3200", 900m, GrantTestData.Kurs),
            GrantTestData.Cost(4, "6800", 999m, GrantTestData.Leir)
        ]);

        Assert.Equal(1500.50m, report.Used);
        Assert.Equal(900m, report.OtherIncome);
        Assert.Equal([6800, 7140], report.UsedByAccount.Select(a => a.Account));
        Assert.Equal(50000m, report.Remaining);
        Assert.Equal(-1500.50m, report.Unused);
    }

    [Fact]
    public void Used_UsesAccountNamesAndListsPostings()
    {
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);
        var report = Build(grant, null,
            [GrantTestData.Cost(2, "6800", 100m, GrantTestData.Kurs, "2027-05-01"), GrantTestData.Cost(1, "6800", 50m, GrantTestData.Kurs, "2027-03-01")],
            [new FikenAccount("6800", "Kontorrekvisita")]);

        var account = Assert.Single(report.UsedByAccount);
        Assert.Equal("Kontorrekvisita", account.AccountName);
        Assert.Equal(150m, account.Amount);
        Assert.Equal([1L, 2L], account.Postings.Select(p => p.JournalEntryId));
    }

    [Fact]
    public void Accounts_OutsideResultRange_AreIgnored()
    {
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);
        var report = Build(grant, null, [GrantTestData.Cost(1, "1500", 400m, GrantTestData.Kurs), GrantTestData.Cost(2, "8800", 400m, GrantTestData.Kurs)]);
        Assert.Equal(0m, report.Used);
    }

    [Fact]
    public void UsePeriod_FiltersPostingsInclusive()
    {
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);
        grant.UseFrom = new DateOnly(2027, 3, 1);
        grant.UseTo = new DateOnly(2027, 6, 30);
        var report = Build(grant, null,
        [
            GrantTestData.Cost(1, "6800", 10m, GrantTestData.Kurs, "2027-02-28"),
            GrantTestData.Cost(2, "6800", 20m, GrantTestData.Kurs, "2027-03-01"),
            GrantTestData.Cost(3, "6800", 40m, GrantTestData.Kurs, "2027-06-30"),
            GrantTestData.Cost(4, "6800", 80m, GrantTestData.Kurs, "2027-07-01")
        ]);
        Assert.Equal(60m, report.Used);
    }

    [Fact]
    public void Shares_ApplyPerProjectAndRoundToOre()
    {
        var a = GrantTestData.Project("A", GrantTestData.Leir);
        var b = GrantTestData.Project("B", GrantTestData.Leir);
        a.ProjectShares = new() { [GrantTestData.Leir] = 33.33m };
        b.ProjectShares = new() { [GrantTestData.Leir] = 66.67m };
        var entries = new[] { GrantTestData.Cost(1, "6800", 100.01m, GrantTestData.Leir) };

        var ra = Build(a, [a, b], entries);
        var rb = Build(b, [a, b], entries);

        Assert.Equal(33.33m, ra.Used);   // 33,3333 -> 33,33
        Assert.Equal(66.68m, rb.Used);   // 66,6767 -> 66,68
        Assert.Equal(100.01m, Assert.Single(ra.UsedByAccount[0].Postings).FullAmount);
        Assert.Empty(ra.Issues);
    }

    [Fact]
    public void SingleGrantOnProject_DefaultsTo100Percent()
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        Assert.Equal(75m, Build(grant, null, [GrantTestData.Cost(1, "6800", 75m, GrantTestData.Leir)]).Used);
    }

    [Fact]
    public void SeveralGrants_OverAllocatedOrMissingShares_AreFlagged()
    {
        var a = GrantTestData.Project("A", GrantTestData.Leir);
        var b = GrantTestData.Project("B", GrantTestData.Leir);
        var projects = new[] { new FikenProject(GrantTestData.Leir, "1", "Landsleir", null, null, null, false) };

        var missing = GrantReportBuilder.CheckProjectShares([a, b], projects);
        var issue = Assert.Single(missing);
        Assert.Equal(GrantIssueKind.ShareMissing, issue.Kind);
        Assert.Contains("Landsleir", issue.Message);

        a.ProjectShares = new() { [GrantTestData.Leir] = 60m };
        b.ProjectShares = new() { [GrantTestData.Leir] = 50m };
        Assert.Equal(GrantIssueKind.ShareOverAllocated, Assert.Single(GrantReportBuilder.CheckProjectShares([a, b], projects)).Kind);

        b.ProjectShares[GrantTestData.Leir] = 40m;
        Assert.Empty(GrantReportBuilder.CheckProjectShares([a, b], projects));

        // Avslåtte tilskudd teller ikke med.
        b.ProjectShares[GrantTestData.Leir] = 50m;
        b.Status = GrantStatus.Rejected;
        Assert.Empty(GrantReportBuilder.CheckProjectShares([a, b], projects));

        // Meldingen kommer også i rapporten for begge tilskuddene.
        b.Status = GrantStatus.Granted;
        Assert.Contains(Build(a, [a, b], []).Issues, i => i.Kind == GrantIssueKind.ShareOverAllocated);
    }

    [Fact]
    public void UsedAboveGranted_IsFlagged()
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        grant.GrantedAmount = 100;
        var report = Build(grant, null, [GrantTestData.Cost(1, "6800", 150m, GrantTestData.Leir)]);
        Assert.Contains(report.Issues, i => i.Kind == GrantIssueKind.UsedExceedsGranted);
    }

    [Fact]
    public void Operating_UsesAccountRangesOnPostingsWithoutProject_PlusManualUse()
    {
        var grant = new Grant
        {
            Name = "Drift 2027",
            Grantor = "Eksempel fylke",
            Kind = GrantKind.Operating,
            GrantedAmount = 10000,
            AccountRanges = [new AccountRange { From = 6800, To = 6899 }],
            ManualUses = [new GrantManualUse { Amount = 500m, Description = "Husleie" }]
        };
        var report = Build(grant, null,
        [
            GrantTestData.Cost(1, "6810", 200m, null),
            GrantTestData.Cost(2, "6810", 300m, GrantTestData.Leir),   // har prosjekt: telles ikke
            GrantTestData.Cost(3, "7000", 400m, null)                   // utenfor intervall
        ]);

        Assert.Equal(700m, report.Used);
        Assert.Equal(500m, report.ManualUsed);
    }

    [Fact]
    public void Operating_WithAccountsButNoPostings_IsZeroNotUnregistered()
    {
        var grant = new Grant { Name = "Drift", Grantor = "X", Kind = GrantKind.Operating, AccountRanges = [new AccountRange { From = 6800, To = 6899 }] };
        Assert.Equal(0m, Build(grant, null, []).Used);
    }

    [Fact]
    public void Operating_ManualOnly_WorksWithoutFikenData()
    {
        var grant = new Grant { Name = "Drift", Grantor = "X", Kind = GrantKind.Operating, ManualUses = [new GrantManualUse { Amount = 250m }] };
        var report = Build(grant, null, null);
        Assert.Equal(250m, report.Used);
        Assert.False(report.HasFikenData);
    }

    [Fact]
    public void Operating_WithNothing_IsUnregistered()
    {
        var grant = new Grant { Name = "Drift", Grantor = "X", Kind = GrantKind.Operating, GrantedAmount = 1000 };
        var report = Build(grant, null, [GrantTestData.Cost(1, "6800", 10m, null)]);
        Assert.Null(report.Used);
        Assert.True(report.UsedIsUnregistered);
        Assert.Null(report.Unused);
        Assert.Equal(1000m, report.Remaining);
    }

    [Fact]
    public void Received_FromFikenAndManual()
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        grant.GrantedAmount = 30000;
        grant.Payments =
        [
            new GrantPayment { JournalEntryId = 10, Note = "ignoreres" },
            new GrantPayment { Date = new DateOnly(2026, 12, 1), Amount = 5000.25m, Note = "Før Fiken" }
        ];
        var report = Build(grant, null, [GrantTestData.Income(10, "3400", 12000m, GrantTestData.Leir, "Eksempel kommune tilskudd")]);

        Assert.Equal(17000.25m, report.Received);
        Assert.Equal(12999.75m, report.Remaining);
        Assert.All(report.Payments, p => Assert.False(p.Missing));
        // Utbetalingen telles ikke som annen inntekt på prosjektet.
        Assert.Equal(0m, report.OtherIncome);
    }

    [Fact]
    public void Received_UsesOnlyIncomeDirection_NotCostLinesOnSameEntry()
    {
        var entry = new FikenJournalEntry(10, 10, "Fri", 10, "Blandet", "2027-04-01",
        [
            new FikenJournalEntryLine(-100000, "3400", null, []),
            new FikenJournalEntryLine(30000, "6800", null, []),
            new FikenJournalEntryLine(70000, "1920", null, [])
        ]);
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        grant.Payments = [new GrantPayment { JournalEntryId = 10 }];
        Assert.Equal(1000m, Build(grant, null, [entry]).Received);
    }

    [Fact]
    public void LinkedPayment_NotInData_IsFlaggedMissing_ButOnlyWhenDataIsSupplied()
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        grant.Payments = [new GrantPayment { JournalEntryId = 777 }];

        var withData = Build(grant, null, []);
        Assert.True(Assert.Single(withData.Payments).Missing);
        Assert.Equal(0m, withData.Received);
        Assert.Contains(withData.Issues, i => i.Kind == GrantIssueKind.PaymentNotFound);

        var noData = Build(grant, null, null);
        Assert.False(Assert.Single(noData.Payments).Missing);
        Assert.Empty(noData.Issues);
    }

    [Fact]
    public void ProjectGrant_WithoutProjects_IsUnregistered()
    {
        var grant = GrantTestData.Project("A");
        Assert.Null(Build(grant, null, [GrantTestData.Cost(1, "6800", 10m, GrantTestData.Leir)]).Used);
    }

    [Fact]
    public void DuplicateEntriesFromOverlappingYears_AreCountedOnce()
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        var entry = GrantTestData.Cost(1, "6800", 100m, GrantTestData.Leir);
        Assert.Equal(100m, Build(grant, null, [entry, entry]).Used);
    }

    [Theory]
    [InlineData("2027-02-10", "2027-05-01", 2027, 2027)]
    [InlineData("2026-11-01", "2028-02-01", 2026, 2028)]
    public void YearsToFetch_CoversUsePeriod(string from, string to, int first, int last)
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        grant.UseFrom = DateOnly.Parse(from);
        grant.UseTo = DateOnly.Parse(to);
        var years = GrantReportBuilder.YearsToFetch(grant, 2027);
        Assert.Equal(Enumerable.Range(first, last - first + 1), years);
    }

    [Fact]
    public void YearsToFetch_OpenPeriod_RunsToCurrentYear_AndNoDatesGivesCurrentYear()
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        Assert.Equal([2027], GrantReportBuilder.YearsToFetch(grant, 2027));

        grant.DecisionDate = new DateOnly(2026, 6, 1);
        Assert.Equal([2026, 2027], GrantReportBuilder.YearsToFetch(grant, 2027));
    }
}

public sealed class GrantPaymentSuggesterTests
{
    private static Grant Grant() => new()
    {
        Name = "Lederkurs 2027",
        Grantor = "Eksempel kommune",
        Status = GrantStatus.Granted,
        GrantedAmount = 20000,
        DecisionDate = new DateOnly(2027, 2, 1),
        ProjectIds = [GrantTestData.Kurs]
    };

    [Fact]
    public void Ranks_AmountMatchFirst_ThenNameSimilarity_ThenDate()
    {
        var grant = Grant();
        var result = GrantPaymentSuggester.Suggest(grant, [grant],
        [
            GrantTestData.Income(1, "3400", 5000m, GrantTestData.Kurs, "Diverse", "2027-03-01"),
            GrantTestData.Income(2, "3400", 5000m, GrantTestData.Kurs, "Tilskudd Eksempel kommune", "2027-05-01"),
            GrantTestData.Income(3, "3900", 20000m, null, "Annet", "2027-06-01"),
            GrantTestData.Income(4, "3400", 5000m, GrantTestData.Kurs, "Diverse", "2027-02-15")
        ]);

        Assert.Equal([3L, 2L, 4L, 1L], result.Select(s => s.JournalEntryId));
        Assert.True(result[0].MatchesAmount);
        Assert.False(result[1].MatchesAmount);
    }

    [Fact]
    public void RemainingAmount_CountsAsAmountMatch()
    {
        var grant = Grant();
        grant.Payments = [new GrantPayment { Amount = 15000m }];
        var result = GrantPaymentSuggester.Suggest(grant, [grant],
        [
            GrantTestData.Income(1, "3400", 1000m, GrantTestData.Kurs, "X", "2027-03-01"),
            GrantTestData.Income(2, "3400", 5000m, GrantTestData.Kurs, "X", "2027-04-01")
        ]);
        Assert.Equal(2, result[0].JournalEntryId);
        Assert.True(result[0].MatchesAmount);
    }

    [Fact]
    public void Includes_ProjectPostings_AndUnprojectedOnBroadAccountsOnly()
    {
        var grant = Grant();
        var result = GrantPaymentSuggester.Suggest(grant, [grant],
        [
            GrantTestData.Income(1, "3200", 100m, GrantTestData.Kurs),     // koblet prosjekt, annen konto: med
            GrantTestData.Income(2, "3499", 100m, null),                    // 34xx uten prosjekt: med
            GrantTestData.Income(3, "3999", 100m, null),                    // 39xx uten prosjekt: med
            GrantTestData.Income(4, "3200", 100m, null),                    // uten prosjekt, annen konto: ikke med
            GrantTestData.Income(5, "3400", 100m, GrantTestData.Leir),      // annet prosjekt: ikke med
            GrantTestData.Cost(6, "6800", 100m, GrantTestData.Kurs),        // kostnad: ikke med
            GrantTestData.Income(7, "3500", 100m, null)                     // 35xx: ikke med
        ]);

        Assert.Equal([1L, 2L, 3L], result.Select(s => s.JournalEntryId).Order());
        Assert.True(result.Single(s => s.JournalEntryId == 1).OnLinkedProject);
        Assert.False(result.Single(s => s.JournalEntryId == 2).OnLinkedProject);
    }

    [Fact]
    public void Excludes_EntriesBeforeDecisionDate_FallingBackToAppliedDate()
    {
        var grant = Grant();
        var entries = new[]
        {
            GrantTestData.Income(1, "3400", 100m, GrantTestData.Kurs, "A", "2027-01-31"),
            GrantTestData.Income(2, "3400", 100m, GrantTestData.Kurs, "B", "2027-02-01")
        };
        Assert.Equal([2L], GrantPaymentSuggester.Suggest(grant, [grant], entries).Select(s => s.JournalEntryId));

        grant.DecisionDate = null;
        grant.AppliedDate = new DateOnly(2027, 1, 1);
        Assert.Equal(2, GrantPaymentSuggester.Suggest(grant, [grant], entries).Count);
    }

    [Fact]
    public void Excludes_EntriesAlreadyLinkedToAnotherGrant()
    {
        var grant = Grant();
        var other = Grant();
        other.Payments = [new GrantPayment { JournalEntryId = 1 }];
        var result = GrantPaymentSuggester.Suggest(grant, [grant, other],
        [
            GrantTestData.Income(1, "3400", 100m, GrantTestData.Kurs),
            GrantTestData.Income(2, "3400", 100m, GrantTestData.Kurs)
        ]);
        Assert.Equal([2L], result.Select(s => s.JournalEntryId));
    }
}

public sealed class GrantAccountOverviewTests
{
    [Fact]
    public void ShowsWhichAccountsLinkedPaymentsSitOn()
    {
        var a = GrantTestData.Project("A", GrantTestData.Leir);
        var b = GrantTestData.Project("B", GrantTestData.Kurs);
        a.Payments = [new GrantPayment { JournalEntryId = 1 }, new GrantPayment { JournalEntryId = 2 }];
        b.Payments = [new GrantPayment { JournalEntryId = 3 }, new GrantPayment { Amount = 99m }];

        var overview = GrantAccountOverview.Build([a, b],
        [
            GrantTestData.Income(1, "3400", 1000m, null),
            GrantTestData.Income(2, "3400", 500m, null),
            GrantTestData.Income(3, "3900", 250m, null),
            GrantTestData.Income(4, "3400", 777m, null)   // ikke koblet
        ],
        [new FikenAccount("3400", "Offentlig tilskudd")]);

        Assert.Collection(overview,
            x =>
            {
                Assert.Equal(3400, x.Account);
                Assert.Equal("Offentlig tilskudd", x.AccountName);
                Assert.Equal(2, x.PaymentCount);
                Assert.Equal(1500m, x.Amount);
            },
            x =>
            {
                Assert.Equal(3900, x.Account);
                Assert.Null(x.AccountName);
                Assert.Equal(1, x.PaymentCount);
                Assert.Equal(250m, x.Amount);
            });
    }
}

public sealed class GrantValidationTests
{
    [Fact]
    public void ValidGrant_HasNoErrors() => Assert.Empty(GrantTestData.Project("Lederkurs", GrantTestData.Kurs).Validate());

    [Fact]
    public void MissingName_IsReported() =>
        Assert.Contains("Tilskuddet mangler navn.", new Grant { Name = " " }.Validate());

    [Theory]
    [InlineData(GrantStatus.Rejected)]
    [InlineData(GrantStatus.Withdrawn)]
    public void GrantedAmount_WithRejectedOrWithdrawn_IsReported(GrantStatus status)
    {
        var grant = GrantTestData.Project("A");
        grant.Status = status;
        grant.GrantedAmount = 1000;
        Assert.Contains(grant.Validate(), e => e.Contains("Innvilget beløp kan ikke være satt"));
    }

    [Fact]
    public void UseFromAfterUseTo_IsReported()
    {
        var grant = GrantTestData.Project("A");
        grant.UseFrom = new DateOnly(2027, 6, 1);
        grant.UseTo = new DateOnly(2027, 5, 1);
        Assert.Contains(grant.Validate(), e => e.Contains("etter at den slutter"));
    }

    [Fact]
    public void BadSharesRangesAndAmounts_AreReported()
    {
        var grant = GrantTestData.Project("A", GrantTestData.Leir);
        grant.ProjectShares = new() { [GrantTestData.Leir] = 120m, [GrantTestData.Kurs] = 50m };
        grant.AccountRanges = [new AccountRange { From = 6900, To = 6800 }];
        grant.ManualUses = [new GrantManualUse { Amount = 0 }];
        grant.Payments = [new GrantPayment { Amount = null }];
        grant.GrantedAmount = -1;

        var errors = grant.Validate();
        Assert.Contains(errors, e => e.Contains("mellom 0 og 100"));
        Assert.Contains(errors, e => e.Contains("ikke er koblet"));
        Assert.Contains(errors, e => e.Contains("6900–6800"));
        Assert.Contains(errors, e => e.Contains("bare driftstilskudd"));
        Assert.Contains(errors, e => e.Contains("manuell utbetaling"));
        Assert.Contains(errors, e => e.Contains("Innvilget beløp kan ikke være negativt"));
    }

    [Fact]
    public void SameJournalEntryOnSeveralGrants_IsReported()
    {
        var a = GrantTestData.Project("A");
        var b = GrantTestData.Project("B");
        a.Payments = [new GrantPayment { JournalEntryId = 55 }];
        b.Payments = [new GrantPayment { JournalEntryId = 55 }];

        var errors = new GrantData { Grants = [a, b] }.Validate();
        Assert.Contains(errors, e => e.Contains("55") && e.Contains("«A»") && e.Contains("«B»"));
    }

    [Fact]
    public void SameJournalEntryTwiceOnOneGrant_IsReported()
    {
        var a = GrantTestData.Project("A");
        a.Payments = [new GrantPayment { JournalEntryId = 55 }, new GrantPayment { JournalEntryId = 55 }];
        Assert.Contains(a.Validate(), e => e.Contains("flere ganger"));
    }
}

public sealed class JsonGrantStoreTests
{
    private static async Task WithStore(Func<JsonGrantStore, string, Task> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fh-grants-" + Guid.NewGuid().ToString("N"));
        try
        {
            await test(new JsonGrantStore(dir), dir);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadWithoutFile_GivesEmptyData() =>
        await WithStore(async (store, _) => Assert.Empty((await store.LoadAsync(TestContext.Current.CancellationToken)).Grants));

    [Fact]
    public async Task SaveAndLoad_RoundTripsAllFields() =>
        await WithStore(async (store, _) =>
        {
            var grant = new Grant
            {
                Name = "Lederkurs 2027",
                Grantor = "Eksempel kommune",
                Scheme = "Opplæring",
                Kind = GrantKind.Operating,
                Status = GrantStatus.PartiallyGranted,
                ApplicationDeadline = new DateOnly(2026, 12, 1),
                AppliedDate = new DateOnly(2026, 11, 20),
                DecisionDate = new DateOnly(2027, 2, 1),
                ReportDeadline = new DateOnly(2028, 1, 31),
                UseFrom = new DateOnly(2027, 1, 1),
                UseTo = new DateOnly(2027, 12, 31),
                AppliedAmount = 50000.5m,
                GrantedAmount = 30000m,
                ProjectIds = [900002],
                ProjectShares = new() { [900002] = 42.5m },
                AccountRanges = [new AccountRange { From = 6800, To = 6899 }],
                ManualUses = [new GrantManualUse { Date = new DateOnly(2027, 3, 3), Amount = 100m, Description = "Husleie" }],
                Payments =
                [
                    new GrantPayment { JournalEntryId = 42, Date = new DateOnly(2027, 4, 1) },
                    new GrantPayment { Date = new DateOnly(2027, 5, 1), Amount = 700m, Note = "Forskudd" }
                ],
                Link = "https://example.no/soknad",
                Notes = "Notat"
            };
            await store.SaveAsync(new GrantData { Grants = [grant] }, TestContext.Current.CancellationToken);

            var loaded = Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Grants);
            Assert.Equal(grant.Id, loaded.Id);
            Assert.Equal(GrantKind.Operating, loaded.Kind);
            Assert.Equal(GrantStatus.PartiallyGranted, loaded.Status);
            Assert.Equal(new DateOnly(2027, 2, 1), loaded.DecisionDate);
            Assert.Equal(50000.5m, loaded.AppliedAmount);
            Assert.Equal(42.5m, loaded.ShareOf(900002));
            Assert.Equal((6800, 6899), (loaded.AccountRanges[0].From, loaded.AccountRanges[0].To));
            Assert.Equal("Husleie", loaded.ManualUses[0].Description);
            Assert.Equal(42, loaded.Payments[0].JournalEntryId);
            Assert.Equal(700m, loaded.Payments[1].Amount);
            Assert.Equal("https://example.no/soknad", loaded.Link);
        });

    [Fact]
    public async Task Normalize_CleansProjectsSharesRangesAndPayments() =>
        await WithStore(async (store, _) =>
        {
            var grant = new Grant
            {
                Name = "  Leir  ",
                Grantor = " Eksempel kommune ",
                Scheme = "   ",
                ProjectIds = [900001, 900001, 0, -3, 900002],
                ProjectShares = new() { [900001] = 50m, [900002] = 0m, [900003] = 40m, [900004] = 101m },
                AccountRanges = [new AccountRange { From = 6899, To = 6800 }, new AccountRange { From = 6800, To = 6899 }, new AccountRange { From = 0, To = 5 }],
                ManualUses = [new GrantManualUse { Amount = 0 }, new GrantManualUse { Amount = 10m, Description = " x " }],
                Payments =
                [
                    new GrantPayment { JournalEntryId = 9, Amount = 123m },
                    new GrantPayment { JournalEntryId = 9 },
                    new GrantPayment { Note = "uten beløp" },
                    new GrantPayment { Amount = 5m }
                ]
            };
            await store.SaveAsync(new GrantData { Grants = [grant, grant] }, TestContext.Current.CancellationToken);

            var loaded = Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Grants);
            Assert.Equal("Leir", loaded.Name);
            Assert.Equal("Eksempel kommune", loaded.Grantor);
            Assert.Null(loaded.Scheme);
            Assert.Equal([900001L, 900002L], loaded.ProjectIds);
            Assert.Equal([900001L], loaded.ProjectShares!.Keys);
            Assert.Equal([(6800, 6899)], loaded.AccountRanges.Select(r => (r.From, r.To)));
            Assert.Equal("x", Assert.Single(loaded.ManualUses).Description);
            Assert.Equal(2, loaded.Payments.Count);
            Assert.Null(loaded.Payments[0].Amount);   // koblet: beløpet hentes fra Fiken
            Assert.Equal(5m, loaded.Payments[1].Amount);
        });

    [Fact]
    public async Task Load_OldFileWithMissingFields_UsesDefaults() =>
        await WithStore(async (store, dir) =>
        {
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(dir, "grants.json"),
                $$"""{"grants":[{"id":"{{id}}","name":"Gammelt","status":"granted","futureField":1},{"name":"Uten id","grantor":null,"projectIds":null}]}""",
                TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, loaded.Grants.Count);
            var old = loaded.Find(id)!;
            Assert.Equal(GrantStatus.Granted, old.Status);
            Assert.Equal(GrantKind.Project, old.Kind);
            Assert.Equal(string.Empty, old.Grantor);
            Assert.Empty(old.ProjectIds);
            Assert.Null(old.ProjectShares);
            Assert.Null(old.GrantedAmount);
            Assert.NotEqual(Guid.Empty, loaded.Grants[1].Id);
        });

    [Fact]
    public async Task Save_LeavesNoTempFiles() =>
        await WithStore(async (store, dir) =>
        {
            await store.SaveAsync(new GrantData { Grants = [GrantTestData.Project("A")] }, TestContext.Current.CancellationToken);
            Assert.Equal(["grants.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
        });
}
