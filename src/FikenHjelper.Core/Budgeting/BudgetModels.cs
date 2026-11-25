namespace FikenHjelper.Core.Budgeting;

/// <summary>Alle årsbudsjetter, lagret lokalt i <c>budgets.json</c>.</summary>
public sealed class BudgetData
{
    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    public List<BudgetYear> Years { get; set; } = [];

    public BudgetYear? FindYear(int year) => Years.FirstOrDefault(y => y.Year == year);

    /// <summary>
    /// Finner poster som ville gått tapt eller blitt slått sammen ved lagring:
    /// poster uten navn og flere poster med samme navn i samme år.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var year in Years)
        {
            if (year.Lines.Any(l => string.IsNullOrWhiteSpace(l.Name)))
            {
                errors.Add($"{year.Year}: En eller flere poster mangler navn.");
            }

            var duplicates = year.Lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Name))
                .GroupBy(l => l.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"«{g.Key}»");
            var list = string.Join(", ", duplicates);
            if (list.Length > 0)
            {
                errors.Add($"{year.Year}: Flere poster har samme navn: {list}.");
            }
        }

        return errors;
    }
}

public sealed class BudgetYear
{
    public int Year { get; set; }
    public List<BudgetLine> Lines { get; set; } = [];

    /// <summary>Kopi med egne poster og prosjektlister, så endringer i kopien ikke påvirker originalen.</summary>
    public BudgetYear Copy() => new()
    {
        Year = Year,
        Lines = Lines.Select(l => new BudgetLine
        {
            Name = l.Name,
            Amount = l.Amount,
            ManualActual = l.ManualActual,
            ProjectIds = [.. l.ProjectIds],
            ProjectShares = l.ProjectShares is null ? null : new Dictionary<long, decimal>(l.ProjectShares)
        }).ToList()
    };
}

/// <summary>
/// Én budsjettpost. Beløp følger samme fortegn som resultatet: inntekter positive, kostnader negative.
/// </summary>
public sealed class BudgetLine
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Budsjettert beløp (inntekt positiv, kostnad negativ).</summary>
    public decimal Amount { get; set; }

    /// <summary>Fiken-prosjekter (projectId) som summeres inn i denne posten for året.</summary>
    public List<long> ProjectIds { get; set; } = [];

    /// <summary>
    /// Fordelingsnøkkel: hvor mange prosent av prosjektets resultat som hører til denne posten, for
    /// prosjekter som er koblet til flere poster. Summen for et prosjekt må være 100 over alle postene.
    /// Prosjekter som bare er koblet til én post, teller helt, uansett hva som står her.
    /// </summary>
    public Dictionary<long, decimal>? ProjectShares { get; set; }

    /// <summary>Andelen i prosent for prosjektet i denne posten, eller null når ingen er satt.</summary>
    public decimal? ShareOf(long projectId) =>
        ProjectShares is not null && ProjectShares.TryGetValue(projectId, out var share) ? share : null;

    /// <summary>Setter eller fjerner (null) andelen for prosjektet.</summary>
    public void SetShare(long projectId, decimal? share)
    {
        if (share is { } value)
        {
            (ProjectShares ??= [])[projectId] = value;
        }
        else if (ProjectShares?.Remove(projectId) == true && ProjectShares.Count == 0)
        {
            ProjectShares = null;
        }
    }

    /// <summary>
    /// Manuelt regnskapstall. Brukes i stedet for Fiken-tall når satt, f.eks. for år før kretsen
    /// tok i bruk prosjekter i Fiken.
    /// </summary>
    public decimal? ManualActual { get; set; }
}

/// <summary>
/// Regnskap for ett prosjekt. For et prosjekt fordelt på flere poster er <see cref="Amount"/> postens del,
/// <see cref="Share"/> andelen i prosent og <see cref="FullAmount"/> hele prosjektets resultat.
/// </summary>
public sealed record ProjectActual(long ProjectId, string Name, decimal Amount)
{
    public decimal? Share { get; init; }
    public decimal? FullAmount { get; init; }
}

/// <summary>Resultat og antall posteringer på resultatkonti for ett prosjekt i ett år.</summary>
public sealed record ProjectActivity(decimal Amount, int PostingCount);

public sealed record BudgetLineReport(
    string Name,
    decimal Budget,
    decimal Actual,
    bool IsManualActual,
    IReadOnlyList<ProjectActual> Projects)
{
    /// <summary>Regnskap minus budsjett. Positivt avvik er gunstig.</summary>
    public decimal Variance => Actual - Budget;

    public decimal? VariancePercent => Budget == 0 ? null : Variance / Math.Abs(Budget);
}

/// <summary>En postering på en resultatkonto uten prosjekt.</summary>
public sealed record UnallocatedPosting(
    long JournalEntryId,
    int? JournalEntryNumber,
    string? Date,
    string? Description,
    string Account,
    decimal Amount);

public sealed record BudgetYearReport(
    int Year,
    IReadOnlyList<BudgetLineReport> Lines,
    IReadOnlyList<UnallocatedPosting> UnallocatedPostings,
    IReadOnlyList<ProjectActual> UnlinkedProjects,
    IReadOnlyList<ProjectSplitIssue> InvalidProjectSplits,
    int PostingsWithSeveralProjects,
    bool HasFikenData)
{
    public decimal TotalBudget => Lines.Sum(l => l.Budget);
    public decimal TotalActual => Lines.Sum(l => l.Actual);
    public decimal TotalVariance => TotalActual - TotalBudget;
    public decimal UnallocatedTotal => UnallocatedPostings.Sum(p => p.Amount);
    public decimal UnlinkedTotal => UnlinkedProjects.Sum(p => p.Amount);

    /// <summary>Årsresultat slik Fiken ser det (alle resultatkonti), eller null uten Fiken-data.</summary>
    public decimal? FikenResult { get; init; }
}
