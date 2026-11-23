using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace FikenHjelper.Core.Fiken;

public sealed partial class FikenClient : IFikenPurchasingClient
{
    private static readonly HashSet<string> InboxStatuses = new(StringComparer.Ordinal) { "unused", "used", "all" };
    private static readonly HashSet<string> EhfStatuses = new(StringComparer.Ordinal) { "unprocessed", "used", "processed" };

    public async Task<IReadOnlyList<FikenInboxDocument>> GetInboxDocumentsAsync(
        string companySlug,
        string status = "unused",
        string? nameContains = null,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        if (!InboxStatuses.Contains(status))
        {
            throw new ArgumentException($"Ugyldig innboksstatus '{status}'.", nameof(status));
        }

        var auth = await ResolveAuthAsync(null, ct);
        var path = $"{CompanyPath(companySlug)}/inbox?status={status}&sortBy={Uri.EscapeDataString("createdDate desc")}";
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            path += $"&name={Uri.EscapeDataString(nameContains.Trim())}";
        }

        return await GetAllPagesAsync<FikenInboxDocument>(path, auth, ct);
    }

    public Task<FikenInboxDocument> GetInboxDocumentAsync(string companySlug, long inboxDocumentId, CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        return GetSingleAsync<FikenInboxDocument>($"{CompanyPath(companySlug)}/inbox/{inboxDocumentId}", ct);
    }

    public async Task<IReadOnlyList<FikenEhfDocument>> GetEhfDocumentsAsync(
        string companySlug,
        string? status = "unprocessed",
        DateOnly? issueDateFrom = null,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        if (status is not null && !EhfStatuses.Contains(status))
        {
            throw new ArgumentException($"Ugyldig EHF-status '{status}'.", nameof(status));
        }

        var auth = await ResolveAuthAsync(null, ct);
        var query = new List<string> { $"sortBy={Uri.EscapeDataString("createdDate desc")}" };
        if (status is not null)
        {
            query.Add($"status={status}");
        }

        if (issueDateFrom is { } from)
        {
            query.Add($"issueDateGe={from:yyyy-MM-dd}");
        }

        return await GetAllPagesAsync<FikenEhfDocument>($"{CompanyPath(companySlug)}/ehf?{string.Join('&', query)}", auth, ct);
    }

    public Task<FikenEhfDocument> GetEhfDocumentAsync(string companySlug, long ehfDocumentId, CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        return GetSingleAsync<FikenEhfDocument>($"{CompanyPath(companySlug)}/ehf/{ehfDocumentId}", ct);
    }

    public async Task<IReadOnlyList<FikenPurchase>> GetPurchasesAsync(
        string companySlug,
        long? contactId = null,
        DateOnly? dateFrom = null,
        DateOnly? dateTo = null,
        bool? paid = null,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var query = new List<string> { $"sortBy={Uri.EscapeDataString("date desc")}" };
        if (contactId is { } id)
        {
            query.Add($"contactId={id}");
        }

        if (dateFrom is { } from)
        {
            query.Add($"dateGe={from:yyyy-MM-dd}");
        }

        if (dateTo is { } to)
        {
            query.Add($"dateLe={to:yyyy-MM-dd}");
        }

        if (paid is { } p)
        {
            query.Add($"paid={(p ? "true" : "false")}");
        }

        return await GetAllPagesAsync<FikenPurchase>($"{CompanyPath(companySlug)}/purchases?{string.Join('&', query)}", auth, ct);
    }

    public async Task<IReadOnlyList<FikenSale>> GetSalesAsync(
        string companySlug,
        bool? settled = null,
        DateOnly? dateFrom = null,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var query = new List<string>();
        if (settled is { } s)
        {
            query.Add($"settled={(s ? "true" : "false")}");
        }

        if (dateFrom is { } from)
        {
            query.Add($"dateGe={from:yyyy-MM-dd}");
        }

        var path = $"{CompanyPath(companySlug)}/sales" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        return await GetAllPagesAsync<FikenSale>(path, auth, ct);
    }

    public async Task<IReadOnlyList<FikenPurchaseDraft>> GetPurchaseDraftsAsync(string companySlug, CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        return await GetAllPagesAsync<FikenPurchaseDraft>($"{CompanyPath(companySlug)}/purchases/drafts", auth, ct);
    }

    public async Task<CreatedResourceResult> CreatePurchaseDraftAsync(
        string companySlug,
        FikenPurchaseDraftRequest request,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines.Count == 0)
        {
            throw new ArgumentException("Kjøpsutkastet må ha minst én linje.", nameof(request));
        }

        var auth = await ResolveAuthAsync(null, ct);
        return await PostForLocationAsync($"{CompanyPath(companySlug)}/purchases/drafts", request, auth, ct);
    }

    public async Task<CreatedResourceResult> AttachDocumentToPurchaseDraftAsync(
        string companySlug,
        long draftId,
        PurchaseDocumentSource source,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsValid)
        {
            throw new ArgumentException("Oppgi nøyaktig ett av innboksdokument eller EHF-dokument.", nameof(source));
        }

        var query = source.InboxDocumentId is { } inboxId
            ? $"inboxDocumentId={inboxId}"
            : $"ehfDocumentId={source.EhfDocumentId}";
        var path = $"{CompanyPath(companySlug)}/purchases/drafts/{draftId}/attachments?{query}";

        var auth = await ResolveAuthAsync(null, ct);
        var (response, _) = await SendWithOAuthRetryAsync(
            token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return req;
            },
            auth,
            ct);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildApiExceptionAsync(response, ct);
            }

            return new CreatedResourceResult(null, response.Headers.Location);
        }
    }

    public async Task<IReadOnlyList<FikenAccount>> GetAccountsAsync(string companySlug, string range, CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        var auth = await ResolveAuthAsync(null, ct);
        return await GetAllPagesAsync<FikenAccount>(
            $"{CompanyPath(companySlug)}/accounts?range={Uri.EscapeDataString(range.Trim())}",
            auth,
            ct);
    }

    public async Task<IReadOnlyList<FikenContact>> GetSuppliersAsync(
        string companySlug,
        string? name = null,
        string? organizationNumber = null,
        CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        var auth = await ResolveAuthAsync(null, ct);
        var path = $"{CompanyPath(companySlug)}/contacts?supplier=true&inactive=false";
        if (!string.IsNullOrWhiteSpace(name))
        {
            path += $"&name={Uri.EscapeDataString(name.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(organizationNumber))
        {
            path += $"&organizationNumber={Uri.EscapeDataString(organizationNumber.Trim())}";
        }

        return await GetAllPagesAsync<FikenContact>(path, auth, ct);
    }

    public Task<FikenContact> GetContactAsync(string companySlug, long contactId, CancellationToken ct = default)
    {
        ValidateSlug(companySlug);
        return GetSingleAsync<FikenContact>($"{CompanyPath(companySlug)}/contacts/{contactId}", ct);
    }

    public async Task<FikenDownloadedFile> DownloadDocumentAsync(string documentUrl, long maxBytes, CancellationToken ct = default)
    {
        if (!IsTrustedFikenUrl(documentUrl, out var uri))
        {
            throw new FikenApiException(
                HttpStatusCode.BadRequest,
                "Dokumentadressen peker ikke til Fiken, så den lastes ikke ned med Fiken-innloggingen.");
        }

        var auth = await ResolveAuthAsync(null, ct);
        var (response, _) = await SendWithOAuthRetryAsync(
            token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return req;
            },
            auth,
            ct);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildApiExceptionAsync(response, ct);
            }

            var length = response.Content.Headers.ContentLength;
            if (length > maxBytes)
            {
                throw new FikenApiException(
                    HttpStatusCode.RequestEntityTooLarge,
                    $"Dokumentet er for stort ({length / 1024} kB, grense {maxBytes / 1024} kB).");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.LongLength > maxBytes)
            {
                throw new FikenApiException(
                    HttpStatusCode.RequestEntityTooLarge,
                    $"Dokumentet er for stort ({bytes.LongLength / 1024} kB, grense {maxBytes / 1024} kB).");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            return new FikenDownloadedFile(bytes, contentType, fileName);
        }
    }

    /// <summary>Bare https-adresser hos fiken.no (inkl. underdomener) regnes som trygge for bearer-token.</summary>
    public static bool IsTrustedFikenUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        var host = parsed.IdnHost.ToLowerInvariant();
        if (host != "fiken.no" && !host.EndsWith(".fiken.no", StringComparison.Ordinal))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private async Task<T> GetSingleAsync<T>(string path, CancellationToken ct)
    {
        var auth = await ResolveAuthAsync(null, ct);
        var (response, _) = await SendWithOAuthRetryAsync(
            token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return req;
            },
            auth,
            ct);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildApiExceptionAsync(response, ct);
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
                ?? throw new FikenApiException(HttpStatusCode.BadGateway, "Tomt svar fra Fiken.");
        }
    }

    private static string CompanyPath(string companySlug) => $"companies/{Uri.EscapeDataString(companySlug)}";
}
