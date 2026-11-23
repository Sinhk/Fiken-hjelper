using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Invoicing;

public enum CustomerMatchConfidence
{
    SavedMapping,
    ExactName,
    NormalizedName,
    ExactEmail,
    FuzzySuggestion,
    Unmatched
}

public sealed record CustomerMatchResult(
    long? MatchedContactId,
    long? SuggestedContactId,
    CustomerMatchConfidence Confidence,
    string MatchReason);

public static partial class CustomerMatcher
{
    private static readonly CultureInfo NorwegianCulture = CultureInfo.GetCultureInfo("nb-NO");

    public static CustomerMatchResult Match(
        string recipientName,
        string? recipientEmail,
        IReadOnlyList<FikenContact> customers,
        IReadOnlyDictionary<string, long> savedMappings)
    {
        var activeCustomers = customers.Where(c => c.Customer && !c.Inactive).ToList();
        if (activeCustomers.Count == 0)
        {
            return new CustomerMatchResult(null, null, CustomerMatchConfidence.Unmatched, "Ingen aktive kunder funnet i Fiken.");
        }

        var trimmedName = (recipientName ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(trimmedName)
            && savedMappings.TryGetValue(trimmedName, out var mappedId))
        {
            var mappedCustomer = activeCustomers.FirstOrDefault(c => c.ContactId == mappedId);
            if (mappedCustomer is not null)
            {
                return new CustomerMatchResult(
                    mappedCustomer.ContactId,
                    mappedCustomer.ContactId,
                    CustomerMatchConfidence.SavedMapping,
                    $"Lagret kobling → {mappedCustomer.Name}");
            }
        }

        if (!string.IsNullOrEmpty(trimmedName))
        {
            var exactMatches = activeCustomers
                .Where(c => string.Equals(c.Name.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exactMatches.Count == 1)
            {
                return new CustomerMatchResult(
                    exactMatches[0].ContactId,
                    exactMatches[0].ContactId,
                    CustomerMatchConfidence.ExactName,
                    $"Eksakt navnetreff → {exactMatches[0].Name}");
            }
        }

        if (!string.IsNullOrWhiteSpace(trimmedName)
            && !trimmedName.Equals("Uten gruppe", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedTarget = NormalizeGroupName(trimmedName);
            if (!string.IsNullOrEmpty(normalizedTarget))
            {
                var normMatches = activeCustomers
                    .Where(c => NormalizeGroupName(c.Name) == normalizedTarget)
                    .ToList();
                if (normMatches.Count == 1)
                {
                    return new CustomerMatchResult(
                        normMatches[0].ContactId,
                        normMatches[0].ContactId,
                        CustomerMatchConfidence.NormalizedName,
                        $"Normalisert gruppetreff → {normMatches[0].Name}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(recipientEmail))
        {
            var emailTrimmed = recipientEmail.Trim();
            var emailMatches = activeCustomers
                .Where(c => !string.IsNullOrWhiteSpace(c.Email)
                    && string.Equals(c.Email.Trim(), emailTrimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (emailMatches.Count == 1)
            {
                return new CustomerMatchResult(
                    emailMatches[0].ContactId,
                    emailMatches[0].ContactId,
                    CustomerMatchConfidence.ExactEmail,
                    $"E-posttreff → {emailMatches[0].Name}");
            }
        }

        if (!string.IsNullOrWhiteSpace(trimmedName)
            && !trimmedName.Equals("Uten gruppe", StringComparison.OrdinalIgnoreCase))
        {
            var targetNorm = NormalizeGroupName(trimmedName);
            if (!string.IsNullOrEmpty(targetNorm))
            {
                FikenContact? bestCustomer = null;
                var bestScore = 0.0;

                foreach (var customer in activeCustomers)
                {
                    var candidateNorm = NormalizeGroupName(customer.Name);
                    if (string.IsNullOrEmpty(candidateNorm))
                    {
                        continue;
                    }

                    var score = ComputeSimilarity(targetNorm, candidateNorm);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestCustomer = customer;
                    }
                }

                if (bestCustomer is not null && bestScore >= 0.65)
                {
                    return new CustomerMatchResult(
                        MatchedContactId: null,
                        SuggestedContactId: bestCustomer.ContactId,
                        Confidence: CustomerMatchConfidence.FuzzySuggestion,
                        MatchReason: $"Forslag: {bestCustomer.Name} (velg/bekreft manuelt)");
                }
            }
        }

        return new CustomerMatchResult(
            null,
            null,
            CustomerMatchConfidence.Unmatched,
            "Ingen treff i Fiken – velg eller opprett kunde");
    }

    public static string NormalizeGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var lower = name.Trim().ToLower(NorwegianCulture);
        lower = PunctuationRegex().Replace(lower, " ");
        lower = SuffixWordsRegex().Replace(lower, " ");
        lower = MultiSpaceRegex().Replace(lower, " ").Trim();
        return lower;
    }

    private static double ComputeSimilarity(string a, string b)
    {
        if (a == b)
        {
            return 1.0;
        }

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var maxLen = Math.Max(a.Length, b.Length);
        var levDist = LevenshteinDistance(a, b);
        var levScore = maxLen == 0 ? 0.0 : 1.0 - (double)levDist / maxLen;

        var setA = tokensA.ToHashSet(StringComparer.Ordinal);
        var setB = tokensB.ToHashSet(StringComparer.Ordinal);
        var intersection = setA.Count(t => setB.Contains(t));
        var union = setA.Union(setB).Count();
        var jaccard = union == 0 ? 0.0 : (double)intersection / union;

        // Penalize differing leading troop numbers (e.g. "1 kristiansund" vs "2 kristiansund").
        if (tokensA.Length > 0 && tokensB.Length > 0
            && int.TryParse(tokensA[0], out var numA)
            && int.TryParse(tokensB[0], out var numB)
            && numA != numB)
        {
            return Math.Min(levScore, 0.45);
        }

        return Math.Max(levScore, jaccard * 0.9);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;

        var prev = new int[t.Length + 1];
        var curr = new int[t.Length + 1];

        for (var j = 0; j <= t.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= s.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[t.Length];
    }

    [GeneratedRegex(@"[.,\-_()/\[\]]+")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\b(speidergruppe|speidargruppe|speiderlag|speidare|speidere|gruppe|nsf|kfuk-kfum|kfuk|kfum)\b")]
    private static partial Regex SuffixWordsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();
}
