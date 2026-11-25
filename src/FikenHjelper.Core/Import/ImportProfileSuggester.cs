using System.Text.RegularExpressions;

namespace FikenHjelper.Core.Import;

/// <summary>
/// Foreslår kolonnemapping ut fra overskriftene (Min speiding-eksportene og vanlige synonymer),
/// og velger beste lagrede profil for en fil.
/// </summary>
public static partial class ImportProfileSuggester
{
    public const string SuggestedProfileName = "Automatisk forslag";

    private static readonly string[] MemberNoKeys = ["medlemsnummer", "medlemsnr", "memberno", "membernumber"];
    private static readonly string[] FullNameKeys = ["navn", "fulltnavn", "name", "fullname", "deltaker", "deltakernavn"];
    private static readonly string[] FirstNameKeys = ["fornavn", "firstname"];
    private static readonly string[] LastNameKeys = ["etternavn", "lastname", "surname"];
    private static readonly string[] EmailKeys = ["epost", "email", "mail", "primaerepost"];
    private static readonly string[] GroupKeys = ["gruppe", "speidergruppe", "gruppenavn", "group"];
    private static readonly string[] GroupIdKeys = ["gruppenummer", "gruppenr", "groupid"];
    private static readonly string[] CategoryKeys = ["deltakeravgift", "avgift", "priskategori", "kategori", "rolle", "billett", "billettype"];
    // «Antall» i Min speidings gruppe-/fullrapport inneholder beløp (f.eks. «0,00»), ikke antall plasser.
    private static readonly string[] AmountKeys = ["totalbelop", "belop", "pris", "sum", "amount", "antall"];
    private static readonly string[] QuantityKeys = ["antallplasser", "quantity"];
    private static readonly string[] CancelledDateKeys = ["avmeldingsdato", "avmeldtdato", "cancelleddate"];
    private static readonly string[] StatusKeys = ["avmeldt", "status"];
    private static readonly string[] ConfirmedKeys = ["bekreftet", "confirmed"];
    private static readonly string[] AttendedKeys = ["tilstede", "oppmott", "attended"];
    private static readonly string[] CheckedInKeys = ["innsjekket", "checkedin"];
    private static readonly string[] TotalAmountKeys = ["totalbelop", "sum", "total", "totalt"];

    // Skjemaspørsmål som typisk inneholder sensitive personopplysninger tas ikke med som standard.
    // Sammenlignes mot starten av hvert ord i overskriften, så «allergier» og «diettbehov» treffes,
    // mens f.eks. «morsomt» ikke treffer «mor».
    private static readonly string[] SensitiveWordStems =
    [
        "mobil", "telefon", "tlf", "fodsel", "adresse", "personnummer", "foresatt", "foreldre", "parorende",
        "allergi", "diett", "intoleranse", "helse", "medisin", "sykdom", "politiattest"
    ];

    private static readonly string[] SensitiveWholeWords = ["mor", "mors", "far", "fars"];
    private static readonly string[] RegistrationDateKeys = ["pameldtdato", "pameldingsdato", "registreringsdato"];
    private static readonly string[] ExtraKeys = ["kommentar", "merknad", "comment"];

    public static ImportProfile Suggest(IReadOnlyList<string> headers)
    {
        var profile = new ImportProfile
        {
            Name = SuggestedProfileName,
            MemberNoColumn = Find(headers, MemberNoKeys),
            FirstNameColumn = Find(headers, FirstNameKeys),
            LastNameColumn = Find(headers, LastNameKeys),
            GroupColumn = Find(headers, GroupKeys),
            GroupIdColumn = Find(headers, GroupIdKeys),
            CategoryColumn = Find(headers, CategoryKeys),
            AmountColumn = Find(headers, AmountKeys),
            QuantityColumn = Find(headers, QuantityKeys),
            CancelledDateColumn = Find(headers, CancelledDateKeys),
            StatusColumn = Find(headers, StatusKeys),
            ConfirmedColumn = Find(headers, ConfirmedKeys),
            AttendedColumn = Find(headers, AttendedKeys),
            CheckedInColumn = Find(headers, CheckedInKeys),
            RegistrationDateColumn = Find(headers, RegistrationDateKeys)
        };

        if (profile.FirstNameColumn is null || profile.LastNameColumn is null)
        {
            profile.FullNameColumn = Find(headers, FullNameKeys);
            if (profile.FullNameColumn is not null)
            {
                profile.FirstNameColumn = null;
                profile.LastNameColumn = null;
            }
        }

        profile.AmountIsTotal = profile.AmountColumn is not null
            && TotalAmountKeys.Contains(ImportValueParser.NormalizeKey(profile.AmountColumn));

        // E-post: alle kolonner som heter «E-post» (Min speiding har to), deretter skjemaets e-postsvar.
        profile.EmailColumns = headers
            .Where(h => EmailKeys.Contains(ImportValueParser.NormalizeKey(StripDuplicateSuffix(h))))
            .Concat(headers.Where(h => ImportValueParser.NormalizeKey(h).EndsWith("epostadresse", StringComparison.Ordinal)
                && !IsGuardianColumn(h)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (profile.StatusColumn is not null)
        {
            profile.CancelledValues = ImportValueParser.NormalizeKey(profile.StatusColumn) == "avmeldt"
                ? ["ja", "x", "1", "true"]
                : ["avmeldt", "kansellert", "cancelled", "declined"];
        }

        var mapped = new HashSet<string>(profile.ReferencedColumns(), StringComparer.OrdinalIgnoreCase);
        profile.ExtraColumns = headers
            .Where(h => !mapped.Contains(h))
            .Where(h => FormQuestionPrefix().IsMatch(h) || ExtraKeys.Contains(ImportValueParser.NormalizeKey(h)))
            .Where(h => !IsSensitive(h))
            .ToList();

        return profile;
    }

    /// <summary>
    /// Velger den lagrede profilen som passer best med filens overskrifter (alle kolonnene den bruker finnes),
    /// ellers et automatisk forslag.
    /// </summary>
    public static (ImportProfile Profile, bool IsSaved) ChooseProfile(
        IReadOnlyList<string> headers,
        IEnumerable<ImportProfile> savedProfiles)
    {
        var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        var best = savedProfiles
            .Select(p => (Profile: p, Columns: p.ReferencedColumns().Except(p.EmailColumns, StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(x => x.Columns.Count > 0
                && x.Columns.All(headerSet.Contains)
                && (x.Profile.EmailColumns.Count == 0 || x.Profile.EmailColumns.Any(headerSet.Contains))
                && HasName(x.Profile))
            .OrderByDescending(x => x.Columns.Count)
            .Select(x => x.Profile)
            .FirstOrDefault();

        return best is not null ? (best.Clone(), true) : (Suggest(headers), false);
    }

    /// <summary>
    /// Forslag til arrangementsnavn fra filnavnet, f.eks.
    /// «Kurshelg høsten 2026, Romsdal og Nordmøre, Deltaker - deltakere.csv» → «Kurshelg høsten 2026».
    /// </summary>
    public static string? SuggestEventTitle(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(fileName.Trim());
        if (GenericReportName().IsMatch(name))
        {
            return null;
        }

        name = DeltakereSuffix().Replace(name, string.Empty);
        var firstComma = name.IndexOf(", ", StringComparison.Ordinal);
        if (firstComma > 0)
        {
            name = name[..firstComma];
        }

        name = name.Trim(' ', '-', '_', ',');
        return name.Length == 0 ? null : name;
    }

    public static bool HasName(ImportProfile profile)
        => !string.IsNullOrWhiteSpace(profile.FullNameColumn)
           || !string.IsNullOrWhiteSpace(profile.FirstNameColumn)
           || !string.IsNullOrWhiteSpace(profile.LastNameColumn);

    private static string? Find(IReadOnlyList<string> headers, string[] keys)
    {
        foreach (var key in keys)
        {
            var match = headers.FirstOrDefault(h => ImportValueParser.NormalizeKey(h) == key);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsSensitive(string header)
        => WordSplitter().Split(header)
            .Select(ImportValueParser.NormalizeKey)
            .Where(w => w.Length > 0)
            .Any(w => SensitiveWholeWords.Contains(w) || SensitiveWordStems.Any(stem => w.Contains(stem, StringComparison.Ordinal)));

    private static string StripDuplicateSuffix(string header) => DuplicateSuffix().Replace(header, string.Empty);

    private static bool IsGuardianColumn(string header)
    {
        var key = ImportValueParser.NormalizeKey(header);
        return key.Contains("foresatt", StringComparison.Ordinal)
            || key.Contains("mors", StringComparison.Ordinal)
            || key.Contains("fars", StringComparison.Ordinal)
            || key.Contains("foreldre", StringComparison.Ordinal)
            || key.Contains("parorende", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex WordSplitter();

    [GeneratedRegex(@"\s\(\d+\)$")]
    private static partial Regex DuplicateSuffix();

    // Spørsmål fra påmeldingsskjema eksporteres som «<Skjema> - <Spørsmål>», f.eks. «Generelt - Hva heter patruljen din?».
    [GeneratedRegex(@"^[^-]{2,60}\s-\s\S")]
    private static partial Regex FormQuestionPrefix();

    [GeneratedRegex(@"^projectParticipantsReport", RegexOptions.IgnoreCase)]
    private static partial Regex GenericReportName();

    [GeneratedRegex(@"(,\s*Deltaker)?\s*-\s*deltakere$", RegexOptions.IgnoreCase)]
    private static partial Regex DeltakereSuffix();
}
