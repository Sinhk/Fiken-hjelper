using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Fiken;

// Modeller for innboks, EHF, kjøp og kjøpsutkast (Fiken API v2). Beløp er i øre (long).

public sealed record FikenInboxDocument(
    [property: JsonPropertyName("documentId")] long DocumentId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("status")] bool Status,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("documentUrl")] string? DocumentUrl);

public sealed record FikenEhfDocumentLine(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("net")] long? Net,
    [property: JsonPropertyName("vat")] long? Vat,
    [property: JsonPropertyName("vatType")] string? VatType);

public sealed record FikenEhfDocument(
    [property: JsonPropertyName("ehfDocumentId")] long EhfDocumentId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("documentType")] string? DocumentType,
    [property: JsonPropertyName("invoiceNumber")] string? InvoiceNumber,
    [property: JsonPropertyName("issueDate")] string? IssueDate,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("supplierName")] string? SupplierName,
    [property: JsonPropertyName("supplierOrganizationNumber")] string? SupplierOrganizationNumber,
    [property: JsonPropertyName("supplierContactId")] long? SupplierContactId,
    [property: JsonPropertyName("net")] long? Net,
    [property: JsonPropertyName("vat")] long? Vat,
    [property: JsonPropertyName("gross")] long? Gross,
    [property: JsonPropertyName("amountDue")] long? AmountDue,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("kid")] string? Kid,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("purchaseId")] long? PurchaseId,
    [property: JsonPropertyName("documentUrl")] string? DocumentUrl,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("lines")] IReadOnlyList<FikenEhfDocumentLine>? Lines);

public sealed record FikenAttachment(
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("type")] string? Type);

public sealed record FikenProjectRef(
    [property: JsonPropertyName("projectId")] long ProjectId,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("name")] string? Name);

public sealed record FikenOrderLine(
    [property: JsonPropertyName("lineId")] long? LineId,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("netPrice")] long? NetPrice,
    [property: JsonPropertyName("vat")] long? Vat,
    [property: JsonPropertyName("account")] string? Account,
    [property: JsonPropertyName("vatType")] string? VatType,
    [property: JsonPropertyName("projectId")] long? ProjectId);

public sealed record FikenPayment(
    [property: JsonPropertyName("paymentId")] long? PaymentId,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("amount")] long? Amount);

public sealed record FikenPurchase(
    [property: JsonPropertyName("purchaseId")] long PurchaseId,
    [property: JsonPropertyName("transactionId")] long? TransactionId,
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("paid")] bool Paid,
    [property: JsonPropertyName("lines")] IReadOnlyList<FikenOrderLine>? Lines,
    [property: JsonPropertyName("supplier")] FikenContact? Supplier,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("payments")] IReadOnlyList<FikenPayment>? Payments,
    [property: JsonPropertyName("purchaseAttachments")] IReadOnlyList<FikenAttachment>? PurchaseAttachments,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("settled")] bool Settled)
{
    /// <summary>Brutto i øre (sum av netto + mva på linjene).</summary>
    public long GrossAmount => (Lines ?? []).Sum(l => (l.NetPrice ?? 0) + (l.Vat ?? 0));
}

public sealed record FikenSale(
    [property: JsonPropertyName("saleId")] long SaleId,
    [property: JsonPropertyName("saleNumber")] string? SaleNumber,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("netAmount")] long? NetAmount,
    [property: JsonPropertyName("vatAmount")] long? VatAmount,
    [property: JsonPropertyName("settled")] bool Settled,
    [property: JsonPropertyName("outstandingBalance")] long? OutstandingBalance,
    [property: JsonPropertyName("customer")] FikenContact? Customer,
    [property: JsonPropertyName("deleted")] bool Deleted);

public sealed record FikenPurchaseDraftLineRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("vatType")] string VatType,
    // Fiken bruker feltnavnet "incomeAccount" også for kostnadskonto på kjøpsutkast.
    [property: JsonPropertyName("incomeAccount")] string Account,
    [property: JsonPropertyName("net")] long Net,
    [property: JsonPropertyName("gross")] long Gross,
    [property: JsonPropertyName("projectId")] long? ProjectId);

/// <summary>
/// Kjøpsutkast. <c>cash</c> og <c>paid</c> er alltid false: utkastet er en ubetalt leverandørfaktura,
/// og betaling/bokføring gjøres manuelt i Fiken.
/// </summary>
public sealed record FikenPurchaseDraftRequest(
    [property: JsonPropertyName("invoiceIssueDate")] string? InvoiceIssueDate,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("invoiceNumber")] string? InvoiceNumber,
    [property: JsonPropertyName("contactId")] long? ContactId,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("kid")] string? Kid,
    [property: JsonPropertyName("lines")] IReadOnlyList<FikenPurchaseDraftLineRequest> Lines)
{
    [JsonPropertyName("cash")]
    public bool Cash => false;

    [JsonPropertyName("paid")]
    public bool Paid => false;
}

public sealed record FikenDraftLineResult(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("vatType")] string? VatType,
    [property: JsonPropertyName("incomeAccount")] string? Account,
    [property: JsonPropertyName("net")] long? Net,
    [property: JsonPropertyName("gross")] long? Gross,
    [property: JsonPropertyName("project")] FikenProjectRef? Project);

public sealed record FikenPurchaseDraft(
    [property: JsonPropertyName("draftId")] long DraftId,
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("invoiceIssueDate")] string? InvoiceIssueDate,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("invoiceNumber")] string? InvoiceNumber,
    [property: JsonPropertyName("contact")] FikenContact? Contact,
    [property: JsonPropertyName("paid")] bool Paid,
    [property: JsonPropertyName("attachments")] IReadOnlyList<FikenAttachment>? Attachments,
    [property: JsonPropertyName("lines")] IReadOnlyList<FikenDraftLineResult>? Lines)
{
    public long GrossAmount => (Lines ?? []).Sum(l => l.Gross ?? 0);
}

/// <summary>Kilde for vedlegg til et kjøpsutkast: et dokument i innboksen eller et mottatt EHF-dokument.</summary>
public sealed record PurchaseDocumentSource(long? InboxDocumentId, long? EhfDocumentId)
{
    public static PurchaseDocumentSource Inbox(long id) => new(id, null);
    public static PurchaseDocumentSource Ehf(long id) => new(null, id);

    public bool IsValid => InboxDocumentId.HasValue ^ EhfDocumentId.HasValue;

    public override string ToString() => InboxDocumentId is { } inbox
        ? $"innboksdokument {inbox}"
        : $"EHF-dokument {EhfDocumentId}";
}

public sealed record FikenDownloadedFile(byte[] Content, string ContentType, string? FileName);
