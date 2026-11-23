namespace FikenHjelper.Core.Budgeting;

/// <summary>Alle årsbudsjetter, lagret lokalt i <c>budgets.json</c>.</summary>
public sealed class BudgetData
{
    public List<BudgetYear> Years { get; set; } = [];

    public BudgetYear? FindYear(int year) => Years.FirstOrDefault(y => y.Year == year);
}

public sealed class BudgetYear
{
    public int Year { get; set; }
    public List<BudgetLine> Lines { get; set; } = [];
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
    /// Manuelt regnskapstall. Brukes i stedet for Fiken-tall når satt, f.eks. for år før kretsen
    /// tok i bruk prosjekter i Fiken.
    /// </summary>
    public decimal? ManualActual { get; set; }
}

public sealed record ProjectActual(long ProjectId, string Name, decimal Amount);

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
    IReadOnlyList<ProjectActual> ProjectsLinkedToSeveralLines,
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
