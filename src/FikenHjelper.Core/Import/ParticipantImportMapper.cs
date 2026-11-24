using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FikenHjelper.Core.MinSpeiding;

namespace FikenHjelper.Core.Import;

public sealed record ImportMapResult(
    MinSpeidingEventLoadResult Event,
    IReadOnlyList<string> Warnings,
    int SourceRowCount,
    int SkippedRowCount);

/// <summary>
/// Gjør om en tabell fra fil (CSV/XLSX) til samme deltakermodell som Min speiding-API-et,
/// slik at prising, kundematching, forhåndsvisning og Fiken-utkast gjenbrukes uendret.
/// </summary>
public static class ParticipantImportMapper
{
    public const string ProjectIdPrefix = "import:";
    public const string DefaultCategoryLabel = "Deltakeravgift";
    private const string UnknownGroupName = "Uten gruppe";
    private const string SourceLabel = "Fil";
    private const int MaxQuantityPerRow = 500;

    private static readonly CultureInfo NorwegianCulture = CultureInfo.GetCultureInfo("nb-NO");
    private static readonly HashSet<string> SummaryRowNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum", "totalt", "total", "summert"
    };

    public static string BuildProjectId(string eventTitle) => ProjectIdPrefix + ImportValueParser.Slugify(eventTitle);

    public static ImportMapResult Map(TabularData table, ImportProfile profile, string eventTitle)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(profile);

        if (!ImportProfileSuggester.HasName(profile))
        {
            throw new InvalidOperationException("Velg hvilken kolonne som inneholder navn (fullt navn eller fornavn/etternavn).");
        }

        var warnings = new List<string>();
        var missing = profile.ReferencedColumns()
            .Concat(profile.ExtraColumns)
            .Where(c => table.IndexOf(c) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
        {
            warnings.Add($"Fant ikke disse kolonnene i filen: {string.Join(", ", missing.Select(m => $"«{m}»"))}.");
        }

        var col = new Columns(table, profile);
        var cancelledValues = profile.CancelledValues
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        var rows = new List<RowData>();
        var skipped = 0;
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var rowNumber = i + 2; // + overskriftsrad, 1-basert som i Excel

            var (firstName, lastName) = ResolveName(table, row, col);
            if (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName))
            {
                skipped++;
                warnings.Add($"Rad {rowNumber} mangler navn og ble hoppet over.");
                continue;
            }

            if (SummaryRowNames.Contains($"{firstName} {lastName}".Trim().TrimEnd(':', '.').Trim()))
            {
                skipped++;
                continue;
            }

            var displayName = $"{firstName} {lastName}".Trim();
            var rawCategory = Cell(table, row, col.Category);
            var rawAmount = Cell(table, row, col.Amount);
            var amount = ImportValueParser.ParseAmountNok(rawAmount);
            if (rawAmount.Length > 0 && amount is null)
            {
                warnings.Add($"Rad {rowNumber} ({displayName}): Klarte ikke å tolke beløpet «{rawAmount}». Kontroller prisen.");
            }

            if (col.Amount < 0 && IsPlainAmount(rawCategory))
            {
                // Kategorikolonnen inneholder bare et beløp («450»): bruk det som beløp.
                amount = ImportValueParser.ParseAmountNok(rawCategory);
                rawCategory = string.Empty;
            }

            var rawQuantity = Cell(table, row, col.Quantity);
            long quantity = 1;
            if (rawQuantity.Length > 0)
            {
                var parsedQuantity = ImportValueParser.ParseLong(rawQuantity);
                if (parsedQuantity == 0)
                {
                    skipped++;
                    warnings.Add($"Rad {rowNumber} ({displayName}): Antall er 0, raden ble hoppet over.");
                    continue;
                }

                if (parsedQuantity is null or < 0 or > MaxQuantityPerRow)
                {
                    warnings.Add($"Rad {rowNumber} ({displayName}): Ugyldig antall «{rawQuantity}», lagt inn som 1 deltaker. Kontroller raden.");
                }
                else if (parsedQuantity > 1)
                {
                    quantity = parsedQuantity.Value;
                    warnings.Add($"Rad {rowNumber} ({displayName}): Antall {quantity} – lagt inn som {quantity} deltakere.");
                }
            }

            if (amount is { } total && quantity > 1 && profile.AmountIsTotal)
            {
                amount = Math.Round(total / quantity, 2, MidpointRounding.AwayFromZero);
                warnings.Add($"Rad {rowNumber} ({displayName}): Totalbeløp {total.ToString("N2", NorwegianCulture)} kr er fordelt på {quantity} deltakere.");
            }

            if (amount < 0)
            {
                warnings.Add($"Rad {rowNumber} ({displayName}): Negativt beløp ({amount.Value.ToString("N2", NorwegianCulture)} kr). Kontroller prisen.");
            }

            var status = Cell(table, row, col.Status);
            var cancelled = !string.IsNullOrWhiteSpace(Cell(table, row, col.CancelledDate))
                || (status.Length > 0 && cancelledValues.Any(v =>
                    status.Equals(v, StringComparison.OrdinalIgnoreCase)
                    || status.StartsWith(v, StringComparison.OrdinalIgnoreCase)));

            var groupName = Cell(table, row, col.Group);
            if (string.IsNullOrWhiteSpace(groupName))
            {
                groupName = string.IsNullOrWhiteSpace(profile.DefaultGroupName) ? UnknownGroupName : profile.DefaultGroupName.Trim();
            }

            var attended = col.Attended >= 0
                ? ImportValueParser.ParseFlag(Cell(table, row, col.Attended))
                : col.CheckedIn < 0 || ImportValueParser.ParseFlag(Cell(table, row, col.CheckedIn));
            var checkedIn = col.CheckedIn >= 0
                ? ImportValueParser.ParseFlag(Cell(table, row, col.CheckedIn))
                : attended;

            rows.Add(new RowData(
                FirstName: firstName,
                LastName: lastName,
                MemberNo: PositiveOrNull(ImportValueParser.ParseLong(Cell(table, row, col.MemberNo))),
                Email: col.Emails.Select(e => Cell(table, row, e)).FirstOrDefault(e => e.Length > 0),
                GroupName: groupName,
                GroupId: PositiveOrNull(ImportValueParser.ParseLong(Cell(table, row, col.GroupId))),
                CategoryLabel: rawCategory.Length > 0 ? rawCategory : null,
                AmountNok: amount ?? (rawCategory.Length > 0 ? ImportValueParser.ExtractAmountFromLabel(rawCategory) : null),
                HasAmountColumnValue: amount.HasValue,
                Quantity: (int)Math.Max(1, quantity),
                Cancelled: cancelled,
                Confirmed: col.Confirmed < 0 || ImportValueParser.ParseFlag(Cell(table, row, col.Confirmed)),
                Attended: attended,
                CheckedIn: checkedIn,
                RegistrationDate: NullIfEmpty(Cell(table, row, col.RegistrationDate)),
                Answers: BuildAnswers(table, row, col.Extras)));
        }

        var categoryByRow = AssignCategories(rows);
        var participants = new List<EventParticipant>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var r = 0; r < rows.Count; r++)
        {
            var data = rows[r];
            var (categoryKey, categoryLabel) = categoryByRow[r];
            var baseKey = BuildBaseKey(data);

            for (var n = 1; n <= data.Quantity; n++)
            {
                var key = UniqueKey(n == 1 ? baseKey : $"{baseKey}#{n}", usedKeys);
                var lastName = data.Quantity > 1 ? $"{data.LastName} ({n}/{data.Quantity})".Trim() : data.LastName;

                participants.Add(new EventParticipant(
                    ParticipantKey: key,
                    MemberNo: data.MemberNo,
                    FirstName: data.FirstName,
                    LastName: lastName,
                    Email: data.Email,
                    GroupName: data.GroupName,
                    GroupId: data.GroupId,
                    TroopName: null,
                    FeeId: null,
                    FeeLabel: categoryLabel,
                    FeeCategoryKey: categoryKey,
                    DetectedPriceNok: data.AmountNok,
                    Cancelled: data.Cancelled,
                    Confirmed: data.Confirmed,
                    Attended: data.Attended,
                    CheckedIn: data.CheckedIn,
                    RegistrationDate: data.RegistrationDate,
                    Answers: data.Answers,
                    ExtraFields: new Dictionary<string, string>(),
                    AvailableGroups: string.Equals(data.GroupName, UnknownGroupName, StringComparison.OrdinalIgnoreCase)
                        ? []
                        : [new ParticipantGroupOption(data.GroupName, data.GroupId, SourceLabel)]));
            }
        }

        var ordered = participants
            .OrderBy(p => p.GroupName, StringComparer.Create(NorwegianCulture, ignoreCase: true))
            .ThenBy(p => p.LastName, StringComparer.Create(NorwegianCulture, ignoreCase: true))
            .ThenBy(p => p.FirstName, StringComparer.Create(NorwegianCulture, ignoreCase: true))
            .ToList();

        var title = string.IsNullOrWhiteSpace(eventTitle) ? "Importert arrangement" : eventTitle.Trim();
        var feeLabels = ordered
            .GroupBy(p => p.FeeCategoryKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FeeLabel, StringComparer.OrdinalIgnoreCase);

        var result = new MinSpeidingEventLoadResult(
            ProjectId: BuildProjectId(title),
            EventTitle: title,
            Participants: ordered,
            FeeCategories: MinSpeidingParser.SummarizeFeeCategories(ordered),
            FeeLabels: feeLabels,
            ExtraParticipantPropertyNames: [],
            QuestionsEndpointAvailable: false,
            IsDemoData: false);

        return new ImportMapResult(result, warnings, table.Rows.Count, skipped);
    }

    /// <summary>
    /// Priskategori per rad. Har aktive (ikke avmeldte) deltakere i samme kategori ulike beløp, deles kategorien
    /// opp («Speider (450 kr)» / «Speider (300 kr)») slik at prisforslaget per kategori blir riktig.
    /// Nøklene er stabile mellom opplastinger så lagrede priser/navn/ekskluderinger treffer igjen.
    /// </summary>
    private static List<(string Key, string Label)> AssignCategories(List<RowData> rows)
    {
        static string Normalize(string label) => label.Trim().ToLowerInvariant();

        var activeAmountsByLabel = rows
            .Where(r => r.CategoryLabel is not null && !r.Cancelled && r.AmountNok.HasValue)
            .GroupBy(r => Normalize(r.CategoryLabel!))
            .ToDictionary(g => g.Key, g => g.Select(r => r.AmountNok!.Value).ToHashSet());

        // Ulike kategorinavn som gir samme slug («Dag 1 + 2» / «Dag 1 / 2») må ikke slås sammen.
        var labelsBySlug = rows
            .Where(r => r.CategoryLabel is not null)
            .Select(r => Normalize(r.CategoryLabel!))
            .Distinct()
            .GroupBy(ImportValueParser.Slugify)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<(string, string)>(rows.Count);
        foreach (var row in rows)
        {
            if (row.CategoryLabel is null)
            {
                result.Add(row.AmountNok is { } amount
                    ? ($"{ProjectIdPrefix}avgift:{FormatKeyAmount(amount)}", $"{DefaultCategoryLabel} {FormatNok(amount)}")
                    : ($"{ProjectIdPrefix}{ImportValueParser.Slugify(DefaultCategoryLabel)}", DefaultCategoryLabel));
                continue;
            }

            var label = row.CategoryLabel.Trim();
            var normalized = Normalize(label);
            var slug = ImportValueParser.Slugify(normalized);
            var baseKey = labelsBySlug.GetValueOrDefault(slug) > 1
                ? $"{ProjectIdPrefix}{slug}-{ShortHash(normalized)}"
                : $"{ProjectIdPrefix}{slug}";

            if (row.AmountNok is { } rowAmount
                && activeAmountsByLabel.TryGetValue(normalized, out var activeAmounts)
                && activeAmounts.Count > 1
                && activeAmounts.Contains(rowAmount))
            {
                var splitLabel = ImportValueParser.ExtractAmountFromLabel(label) == rowAmount
                    ? label
                    : $"{label} ({FormatNok(rowAmount)})";
                result.Add(($"{baseKey}:{FormatKeyAmount(rowAmount)}", splitLabel));
            }
            else
            {
                result.Add((baseKey, label));
            }
        }

        return result;
    }

    private static (string FirstName, string LastName) ResolveName(TabularData table, IReadOnlyList<string> row, Columns col)
    {
        var first = Cell(table, row, col.FirstName);
        var last = Cell(table, row, col.LastName);
        if (first.Length > 0 || last.Length > 0)
        {
            return (first, last);
        }

        var full = Cell(table, row, col.FullName);
        if (full.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        // «Etternavn, Fornavn» eller «Fornavn Mellomnavn Etternavn».
        var comma = full.IndexOf(',');
        if (comma > 0)
        {
            return (full[(comma + 1)..].Trim(), full[..comma].Trim());
        }

        var normalized = string.Join(' ', full.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var lastSpace = normalized.LastIndexOf(' ');
        return lastSpace > 0
            ? (normalized[..lastSpace], normalized[(lastSpace + 1)..])
            : (normalized, string.Empty);
    }

    private static string BuildBaseKey(RowData data)
    {
        if (data.MemberNo is { } memberNo)
        {
            return $"m:{memberNo.ToString(CultureInfo.InvariantCulture)}";
        }

        return $"r:{ShortHash($"{data.FirstName} {data.LastName}|{data.Email}|{data.GroupName}".ToLowerInvariant(), 6)}";
    }

    private static string ShortHash(string value, int bytes = 3)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, bytes).ToLowerInvariant();

    private static long? PositiveOrNull(long? value) => value > 0 ? value : null;

    private static string UniqueKey(string key, HashSet<string> used)
    {
        var candidate = key;
        var n = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{key}~{n++}";
        }

        return candidate;
    }

    private static IReadOnlyDictionary<string, string> BuildAnswers(TabularData table, IReadOnlyList<string> row, IReadOnlyList<(string Header, int Index)> extras)
    {
        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (header, index) in extras)
        {
            var value = Cell(table, row, index);
            if (value.Length == 0)
            {
                continue;
            }

            // «Generelt - Hva heter patruljen din?» → «Hva heter patruljen din?»
            var dash = header.IndexOf(" - ", StringComparison.Ordinal);
            var key = dash > 0 && dash + 3 < header.Length ? header[(dash + 3)..].Trim() : header;
            if (!answers.TryAdd(key, value))
            {
                answers.TryAdd(header, value);
            }
        }

        return answers;
    }

    private static bool IsPlainAmount(string value)
        => value.Length > 0 && !value.Any(char.IsLetter) && ImportValueParser.ParseAmountNok(value).HasValue;

    /// <summary>Celleverdi, der Min speidings plassholder «-» for tomt felt regnes som tom.</summary>
    private static string Cell(TabularData table, IReadOnlyList<string> row, int index)
    {
        var value = index < 0 ? string.Empty : table.GetCell(row, index).Trim();
        return value is "-" or "–" or "—" ? string.Empty : value;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string FormatNok(decimal amount)
        => amount == decimal.Truncate(amount)
            ? $"{amount.ToString("N0", NorwegianCulture)} kr"
            : $"{amount.ToString("N2", NorwegianCulture)} kr";

    private static string FormatKeyAmount(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record RowData(
        string FirstName,
        string LastName,
        long? MemberNo,
        string? Email,
        string GroupName,
        long? GroupId,
        string? CategoryLabel,
        decimal? AmountNok,
        bool HasAmountColumnValue,
        int Quantity,
        bool Cancelled,
        bool Confirmed,
        bool Attended,
        bool CheckedIn,
        string? RegistrationDate,
        IReadOnlyDictionary<string, string> Answers);

    private sealed class Columns
    {
        public Columns(TabularData table, ImportProfile p)
        {
            FullName = table.IndexOf(p.FullNameColumn);
            FirstName = table.IndexOf(p.FirstNameColumn);
            LastName = table.IndexOf(p.LastNameColumn);
            MemberNo = table.IndexOf(p.MemberNoColumn);
            Emails = p.EmailColumns.Select(table.IndexOf).Where(i => i >= 0).ToList();
            Group = table.IndexOf(p.GroupColumn);
            GroupId = table.IndexOf(p.GroupIdColumn);
            Category = table.IndexOf(p.CategoryColumn);
            Amount = table.IndexOf(p.AmountColumn);
            Quantity = table.IndexOf(p.QuantityColumn);
            CancelledDate = table.IndexOf(p.CancelledDateColumn);
            Status = table.IndexOf(p.StatusColumn);
            Confirmed = table.IndexOf(p.ConfirmedColumn);
            Attended = table.IndexOf(p.AttendedColumn);
            CheckedIn = table.IndexOf(p.CheckedInColumn);
            RegistrationDate = table.IndexOf(p.RegistrationDateColumn);
            Extras = p.ExtraColumns
                .Select(h => (Header: h, Index: table.IndexOf(h)))
                .Where(x => x.Index >= 0)
                .ToList();
        }

        public int FullName { get; }
        public int FirstName { get; }
        public int LastName { get; }
        public int MemberNo { get; }
        public IReadOnlyList<int> Emails { get; }
        public int Group { get; }
        public int GroupId { get; }
        public int Category { get; }
        public int Amount { get; }
        public int Quantity { get; }
        public int CancelledDate { get; }
        public int Status { get; }
        public int Confirmed { get; }
        public int Attended { get; }
        public int CheckedIn { get; }
        public int RegistrationDate { get; }
        public IReadOnlyList<(string Header, int Index)> Extras { get; }
    }
}
