using System.ComponentModel;
using FikenHjelper.Core.Analysis;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Mcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FikenHjelper.Mcp.Tools;

[McpServerToolType]
public sealed class BudgetTools(
    BudgetReportService reports,
    IBudgetStore budgetStore,
    FikenContextService context,
    FikenLookups lookups,
    TimeProvider time)
{
    internal const string SignNote =
        "Fortegn: inntekt positiv, kostnad negativ (resultat = −beløp på konto 3000–8799, som i budsjettrapporten). " +
        "Positivt avvik er gunstig.";

    [McpServerTool(Name = "get_budget_status", Title = "Budsjett mot regnskap", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Henter budsjett mot regnskap per budsjettpost (fra budsjettet i Fiken-hjelper og posteringer i Fiken), med avvik i kr og %, prosjekter per post og kontrolltall (ufordelte posteringer, ukoblede prosjekter). Kan vise flere år side om side.")]
    public Task<object> GetBudgetStatus(
        [Description("Siste år som tas med. Standard: inneværende år.")] int? year = null,
        [Description("Antall år bakover, inkludert det siste (1-5).")] int years = 1,
        [Description("Hent ferske tall fra Fiken i stedet for mellomlagrede (maks 15 min gamle).")] bool refresh = false,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        var lastYear = year ?? ToolSupport.Today(time).Year;
        var count = Math.Clamp(years, 1, 5);
        var comparison = await reports.BuildComparisonAsync(Enumerable.Range(0, count).Select(i => lastYear - i), refresh, ct);

        return new
        {
            note = SignNote,
            years = comparison.Years.Select(y =>
            {
                var r = comparison.Reports[y];
                return new
                {
                    year = y,
                    hasFikenData = r.HasFikenData,
                    totals = new { budget = r.TotalBudget, actual = r.TotalActual, variance = r.TotalVariance, fikenResult = r.FikenResult },
                    lines = r.Lines.Select(l => new
                    {
                        l.Name,
                        budget = l.Budget,
                        actual = l.Actual,
                        variance = l.Variance,
                        variancePercent = l.VariancePercent is { } p ? Math.Round(p * 100, 1) : (decimal?)null,
                        manualActual = l.IsManualActual,
                        projects = l.Projects.Select(p => new { p.ProjectId, p.Name, amount = p.Amount }).ToList()
                    }).ToList(),
                    control = new
                    {
                        unallocatedPostings = r.UnallocatedPostings.Count,
                        unallocatedTotal = r.UnallocatedTotal,
                        unlinkedProjects = r.UnlinkedProjects.Select(p => new { p.ProjectId, p.Name, amount = p.Amount }).ToList(),
                        projectsLinkedToSeveralLines = r.ProjectsLinkedToSeveralLines.Select(p => new { p.ProjectId, p.Name }).ToList(),
                        r.PostingsWithSeveralProjects
                    }
                };
            }).ToList()
        };
    });

    [McpServerTool(Name = "explain_project_variance", Title = "Forklar avvik", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Forklarer avviket for én budsjettpost eller ett prosjekt: budsjett mot regnskap, sum per konto (med fjorårstall) og de største enkeltposteringene. Oppgi budgetLineName eller projectId.")]
    public Task<object> ExplainProjectVariance(
        [Description("Navnet på budsjettposten (som i get_budget_status).")] string? budgetLineName = null,
        [Description("Fiken-prosjekt (projectId) hvis du vil se på ett prosjekt.")] long? projectId = null,
        [Description("År. Standard: inneværende år.")] int? year = null,
        [Description("Sammenlign med året før.")] bool compareWithPreviousYear = true,
        [Description("Maks antall enkeltposteringer (0-100).")] int maxPostings = 30,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        if (string.IsNullOrWhiteSpace(budgetLineName) == (projectId is null))
        {
            throw new ArgumentException("Oppgi enten budgetLineName eller projectId.");
        }

        var y = year ?? ToolSupport.Today(time).Year;
        var slug = await context.GetCompanySlugAsync(ct);
        var budget = await budgetStore.LoadAsync(ct);

        BudgetLine? line = null;
        BudgetLine? previousLine = null;
        IReadOnlyCollection<long> projectIds;
        IReadOnlyCollection<long> previousProjectIds;
        if (budgetLineName is not null)
        {
            line = FindLine(budget.FindYear(y), budgetLineName, y);
            projectIds = line.ProjectIds.Distinct().ToList();
            previousLine = budget.FindYear(y - 1)?.Lines.FirstOrDefault(l =>
                string.Equals(l.Name, line.Name, StringComparison.CurrentCultureIgnoreCase));
            previousProjectIds = previousLine?.ProjectIds.Distinct().ToList() ?? projectIds;
        }
        else
        {
            projectIds = [projectId!.Value];
            previousProjectIds = projectIds;
        }

        var notes = new List<string> { SignNote };
        if (line is { ManualActual: not null })
        {
            notes.Add("Budsjettposten har et manuelt regnskapstall; Fiken-tallene under brukes ikke i budsjettrapporten.");
        }

        if (projectIds.Count == 0)
        {
            notes.Add($"Budsjettposten er ikke koblet til noen Fiken-prosjekter i {y}.");
        }

        var entries = await reports.GetJournalEntriesAsync(slug, y, refresh: false, ct);
        var previousEntries = compareWithPreviousYear ? await reports.GetJournalEntriesAsync(slug, y - 1, refresh: false, ct) : null;
        var accounts = await lookups.GetAccountsAsync(slug, "3000-8799", ct);
        var projects = await lookups.GetProjectsAsync(slug, ct);

        var explanation = ProjectVarianceExplainer.Explain(y, projectIds, entries, null, accounts, maxPostings);
        var previous = previousEntries is null
            ? null
            : ProjectVarianceExplainer.Explain(y - 1, previousProjectIds, previousEntries, null, accounts, 0);
        var previousByAccount = previous?.ByAccount.ToDictionary(a => a.Account, a => a.Amount);
        var projectNames = projects.GroupBy(p => p.ProjectId).ToDictionary(g => g.Key, g => g.First().Name);

        var byAccount = explanation.ByAccount
            .Select(a => a.Account)
            .Concat(previousByAccount?.Keys ?? Enumerable.Empty<string>())
            .Distinct()
            .Select(account =>
            {
                var current = explanation.ByAccount.FirstOrDefault(a => a.Account == account);
                var prev = previousByAccount?.GetValueOrDefault(account);
                return new
                {
                    account,
                    name = current?.AccountName ?? accounts.FirstOrDefault(a => a.Code == account)?.Name,
                    amount = current?.Amount ?? 0,
                    previousYear = prev,
                    change = prev is null ? (decimal?)null : (current?.Amount ?? 0) - prev.Value,
                    postings = current?.Postings ?? 0
                };
            })
            .OrderBy(a => a.amount)
            .ToList();

        return new
        {
            year = y,
            budgetLine = line?.Name,
            budget = line?.Amount,
            actual = explanation.Actual,
            variance = line is null ? (decimal?)null : explanation.Actual - line.Amount,
            previousYear = previous is null ? null : new
            {
                year = y - 1,
                budget = previousLine?.Amount,
                actual = previous.Actual,
                projects = previousProjectIds.Select(id => new { projectId = id, name = projectNames.GetValueOrDefault(id) }).ToList()
            },
            projects = projectIds.Select(id => new { projectId = id, name = projectNames.GetValueOrDefault(id) }).ToList(),
            explanation.PostingCount,
            byAccount,
            largestPostings = explanation.LargestPostings.Select(p => new
            {
                p.Date,
                p.JournalEntryNumber,
                p.Description,
                p.Account,
                project = projectNames.GetValueOrDefault(p.ProjectId),
                amount = p.Amount
            }).ToList(),
            notes
        };
    });

    private static BudgetLine FindLine(BudgetYear? budgetYear, string name, int year)
    {
        if (budgetYear is null || budgetYear.Lines.Count == 0)
        {
            throw new McpException($"Det finnes ikke noe budsjett for {year} i Fiken-hjelper.");
        }

        var trimmed = name.Trim();
        var exact = budgetYear.Lines.Where(l => string.Equals(l.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count > 1)
        {
            var ids = string.Join("; ", exact.Select(l => $"prosjekter {string.Join(", ", l.ProjectIds)}"));
            throw new McpException($"Det finnes {exact.Count} budsjettposter som heter «{trimmed}» i {year} ({ids}). Bruk projectId i stedet.");
        }

        var partial = budgetYear.Lines.Where(l => l.Name.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (partial.Count == 1)
        {
            return partial[0];
        }

        var options = string.Join(", ", (partial.Count > 1 ? partial : budgetYear.Lines).Select(l => $"«{l.Name}»"));
        throw new McpException(partial.Count > 1
            ? $"Flere budsjettposter passer: {options}. Oppgi hele navnet."
            : $"Fant ikke budsjettposten «{trimmed}» i {year}. Poster: {options}.");
    }
}
