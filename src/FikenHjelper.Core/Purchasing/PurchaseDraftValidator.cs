using System.Globalization;
using System.Text.RegularExpressions;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Purchasing;

public sealed record PurchaseDraftLineInput(string Text, string Account, decimal AmountKr, long? ProjectId);

public sealed record PurchaseDraftInput(
    PurchaseDocumentSource? Source,
    long? SupplierContactId,
    string? InvoiceNumber,
    DateOnly? IssueDate,
    DateOnly? DueDate,
    string? Kid,
    IReadOnlyList<PurchaseDraftLineInput> Lines);

/// <summary>Data fra Fiken som trengs for å validere et kjøpsutkast.</summary>
public sealed record PurchaseDraftContext(
    IReadOnlyList<FikenAccount> Accounts,
    IReadOnlyList<FikenProject> Projects,
    IReadOnlyList<FikenPurchaseDraft> ExistingDrafts,
    IReadOnlyList<FikenPurchase> SupplierPurchases,
    FikenContact? Supplier,
    FikenEhfDocument? EhfDocument,
    FikenInboxDocument? InboxDocument,
    string VatType,
    DateOnly Today);

public sealed record PurchaseDraftValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    FikenPurchaseDraftRequest? Request,
    decimal TotalKr)
{
    public bool IsValid => Errors.Count == 0 && Request is not null;
}

/// <summary>
/// Validerer og normaliserer et kjøpsutkast før det vises til brukeren. Resultatet (Request) er nøyaktig det
/// som sendes til Fiken hvis brukeren bekrefter.
/// </summary>
public static partial class PurchaseDraftValidator
{
    public const int MaxLines = 50;
    public const int MaxTextLength = 200;
    public const int FirstExpenseAccount = 4000;
    public const int LastExpenseAccount = 7999;
    public const int LastAllowedAccount = 8999;

    /// <summary>Kretsen er ikke MVA-registrert. Må verifiseres i testforetak før bruk mot ekte regnskap.</summary>
    public const string DefaultVatType = "NONE";

    public static PurchaseDraftValidation Validate(PurchaseDraftInput input, PurchaseDraftContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var errors = new List<string>();
        var warnings = new List<string>();

        var accountCodes = new HashSet<string>(context.Accounts.Select(a => a.Code), StringComparer.Ordinal);
        var projects = context.Projects.GroupBy(p => p.ProjectId).ToDictionary(g => g.Key, g => g.First());
        var ehf = context.EhfDocument;

        // Kilde
        if (input.Source is { IsValid: false })
        {
            errors.Add("Oppgi enten innboksdokument eller EHF-dokument som vedlegg, ikke begge.");
        }
        else if (input.Source is null)
        {
            warnings.Add("Utkastet får ikke vedlegg. Husk å legge ved bilaget i Fiken.");
        }

        if (ehf is not null)
        {
            if (string.Equals(ehf.DocumentType, "creditNote", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("EHF-dokumentet er en kreditnota. Kreditnotaer støttes ikke av dette verktøyet ennå; behandle den i Fiken.");
            }

            if (!string.Equals(ehf.Status, "unprocessed", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"EHF-dokumentet har status «{ehf.Status}» og er allerede behandlet. Velg et ubehandlet dokument, eller behandle det i Fiken.");
            }

            if (!string.IsNullOrWhiteSpace(ehf.Currency) && !string.Equals(ehf.Currency, "NOK", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"EHF-dokumentet er i {ehf.Currency}. Bare NOK støttes; behandle fakturaen i Fiken.");
            }
        }

        if (context.InboxDocument is { Status: true })
        {
            errors.Add("Innboksdokumentet er allerede brukt som bilag et annet sted. Velg et annet dokument, eller sjekk i Fiken.");
        }

        // Leverandør
        if (input.SupplierContactId is { } given && ehf?.SupplierContactId is { } fromEhf && given != fromEhf)
        {
            warnings.Add($"Valgt leverandør (kontakt {given}) er ikke den Fiken har koblet til EHF-dokumentet (kontakt {fromEhf}).");
        }

        var contactId = input.SupplierContactId ?? ehf?.SupplierContactId;
        if (contactId is null)
        {
            warnings.Add("Ingen leverandør valgt. Utkastet opprettes uten leverandør, så velg leverandør i Fiken før bokføring.");
        }
        else if (context.Supplier is null)
        {
            errors.Add($"Fant ikke leverandør (kontakt) med id {contactId} i Fiken.");
        }
        else if (!context.Supplier.Supplier)
        {
            warnings.Add($"Kontakten «{context.Supplier.Name}» er ikke merket som leverandør i Fiken.");
        }

        // Datoer, fakturanummer, KID
        var issueDate = input.IssueDate ?? ParseDate(ehf?.IssueDate);
        var dueDate = input.DueDate ?? ParseDate(ehf?.DueDate);
        if (issueDate is null)
        {
            errors.Add("Fakturadato mangler.");
        }
        else if (issueDate.Value.DayNumber > context.Today.DayNumber + 1)
        {
            warnings.Add($"Fakturadatoen {issueDate:yyyy-MM-dd} er frem i tid.");
        }

        if (issueDate is not null && dueDate is not null && dueDate < issueDate)
        {
            errors.Add("Forfallsdato er før fakturadato.");
        }

        var invoiceNumber = Clean(input.InvoiceNumber) ?? Clean(ehf?.InvoiceNumber);
        var kid = Clean(input.Kid) ?? Clean(ehf?.Kid);
        if (kid is not null && !KidPattern().IsMatch(kid))
        {
            errors.Add("KID må være 2–25 siffer.");
        }

        // Linjer
        var lines = new List<FikenPurchaseDraftLineRequest>();
        if (input.Lines.Count == 0)
        {
            errors.Add("Utkastet må ha minst én linje.");
        }
        else if (input.Lines.Count > MaxLines)
        {
            errors.Add($"For mange linjer (maks {MaxLines}).");
        }

        for (var i = 0; i < input.Lines.Count && i < MaxLines; i++)
        {
            var line = input.Lines[i];
            var label = $"Linje {i + 1}";
            var text = Clean(line.Text);
            if (text is null)
            {
                errors.Add($"{label}: tekst mangler.");
                text = string.Empty;
            }
            else if (text.Length > MaxTextLength)
            {
                warnings.Add($"{label}: teksten er kortet ned til {MaxTextLength} tegn.");
                text = text[..MaxTextLength];
            }

            var account = Clean(line.Account) ?? string.Empty;
            if (!AccountPattern().IsMatch(account))
            {
                errors.Add($"{label}: ugyldig kontonummer «{line.Account}».");
            }
            else
            {
                if (!accountCodes.Contains(account))
                {
                    errors.Add($"{label}: konto {account} finnes ikke i kontoplanen.");
                }

                var main = int.Parse(account.Split(':')[0], CultureInfo.InvariantCulture);
                if (main is < FirstExpenseAccount or > LastAllowedAccount)
                {
                    errors.Add($"{label}: konto {account} er en balanse- eller inntektskonto. Kjøpslinjer må føres på kostnadskonto (4000–7999, evt. 8xxx).");
                }
                else if (main > LastExpenseAccount)
                {
                    warnings.Add($"{label}: konto {account} er ikke en vanlig kostnadskonto (4000–7999).");
                }
            }

            if (!TryToOre(line.AmountKr, out var ore))
            {
                errors.Add($"{label}: beløpet {line.AmountKr.ToString(CultureInfo.InvariantCulture)} kr må være større enn 0 og ha maks to desimaler.");
            }

            if (line.ProjectId is { } projectId)
            {
                if (!projects.TryGetValue(projectId, out var project))
                {
                    errors.Add($"{label}: fant ikke prosjekt {projectId} i Fiken.");
                }
                else if (project.Completed)
                {
                    warnings.Add($"{label}: prosjektet «{project.Name}» er avsluttet.");
                }
            }

            lines.Add(new FikenPurchaseDraftLineRequest(text, context.VatType, account, ore, ore, line.ProjectId));
        }

        var totalOre = lines.Sum(l => l.Gross);
        var totalKr = totalOre / 100m;

        if (ehf?.Gross is { } ehfGross && lines.Count > 0 && ehfGross != totalOre)
        {
            errors.Add($"Sum linjer ({FormatKr(totalOre)}) er ikke lik EHF-fakturaens brutto ({FormatKr(ehfGross)}).");
        }
        else if (input.Source?.InboxDocumentId is not null)
        {
            warnings.Add("Kontroller at summen stemmer med bilaget i innboksen (beløpet kan ikke leses automatisk).");
        }

        // Duplikater
        if (invoiceNumber is not null)
        {
            var duplicateDrafts = context.ExistingDrafts
                .Where(d => string.Equals(Clean(d.InvoiceNumber), invoiceNumber, StringComparison.OrdinalIgnoreCase)
                    && (contactId is null || d.Contact is null || d.Contact.ContactId == contactId))
                .ToList();
            if (duplicateDrafts.Count > 0)
            {
                warnings.Add($"Det finnes allerede kjøpsutkast med fakturanummer {invoiceNumber} (utkast {string.Join(", ", duplicateDrafts.Select(d => d.DraftId))}).");
            }

            var duplicatePurchases = context.SupplierPurchases
                .Where(p => !p.Deleted && string.Equals(Clean(p.Identifier), invoiceNumber, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (duplicatePurchases.Count > 0)
            {
                warnings.Add($"Fakturanummer {invoiceNumber} er allerede bokført som kjøp fra denne leverandøren (kjøp {string.Join(", ", duplicatePurchases.Select(p => p.PurchaseId))}). Sjekk at fakturaen ikke føres dobbelt.");
            }
        }

        if (!string.Equals(context.VatType, DefaultVatType, StringComparison.Ordinal))
        {
            errors.Add($"MVA-type {context.VatType} støttes ikke: kretsen er ikke MVA-registrert, og linjene sendes med netto = brutto.");
        }

        var request = errors.Count == 0
            ? new FikenPurchaseDraftRequest(
                issueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                invoiceNumber,
                contactId,
                "NOK",
                kid,
                lines)
            : null;

        return new PurchaseDraftValidation(errors, warnings, request, totalKr);
    }

    /// <summary>Konverterer kroner til øre. Krever beløp &gt; 0 og maks to desimaler.</summary>
    public static bool TryToOre(decimal amountKr, out long ore)
    {
        ore = 0;
        if (amountKr <= 0 || amountKr > 100_000_000m)
        {
            return false;
        }

        var scaled = amountKr * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            return false;
        }

        ore = (long)scaled;
        return true;
    }

    public static string FormatKr(long ore) =>
        (ore / 100m).ToString("N2", CultureInfo.GetCultureInfo("nb-NO")) + " kr";

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\d{4}(:\d+)?$")]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"^\d{2,25}$")]
    private static partial Regex KidPattern();
}
