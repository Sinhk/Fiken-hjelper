using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Fiken;

public sealed record FikenCompany(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("organizationNumber")] string? OrganizationNumber,
    [property: JsonPropertyName("vatType")] string? VatType,
    [property: JsonPropertyName("hasApiAccess")] bool HasApiAccess,
    [property: JsonPropertyName("testCompany")] bool TestCompany);

public sealed record FikenContact(
    [property: JsonPropertyName("contactId")] long ContactId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("organizationNumber")] string? OrganizationNumber,
    [property: JsonPropertyName("customerNumber")] int? CustomerNumber,
    [property: JsonPropertyName("customerAccountCode")] string? CustomerAccountCode,
    [property: JsonPropertyName("customer")] bool Customer,
    [property: JsonPropertyName("supplier")] bool Supplier,
    [property: JsonPropertyName("inactive")] bool Inactive,
    [property: JsonPropertyName("daysUntilInvoicingDueDate")] int? DaysUntilInvoicingDueDate,
    [property: JsonPropertyName("groups")] IReadOnlyList<string>? Groups);

public sealed record FikenCreateContactRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("customer")] bool Customer = true,
    [property: JsonPropertyName("organizationNumber")] string? OrganizationNumber = null);

public sealed record FikenProject(
    [property: JsonPropertyName("projectId")] long ProjectId,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("startDate")] string? StartDate,
    [property: JsonPropertyName("endDate")] string? EndDate,
    [property: JsonPropertyName("completed")] bool Completed);

public sealed record FikenAccount(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name);

public sealed record FikenBankAccount(
    [property: JsonPropertyName("bankAccountId")] long BankAccountId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("accountCode")] string? AccountCode,
    [property: JsonPropertyName("bankAccountNumber")] string? BankAccountNumber,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("inactive")] bool Inactive);

public sealed record FikenInvoiceDraftLineRequest(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("unitPrice")] long? UnitPrice,
    [property: JsonPropertyName("vatType")] string? VatType,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("incomeAccount")] string? IncomeAccount,
    [property: JsonPropertyName("comment")] string? Comment = null);

public sealed record FikenInvoiceDraftRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("issueDate")] string? IssueDate,
    [property: JsonPropertyName("daysUntilDueDate")] int DaysUntilDueDate,
    [property: JsonPropertyName("invoiceText")] string? InvoiceText,
    [property: JsonPropertyName("yourReference")] string? YourReference,
    [property: JsonPropertyName("ourReference")] string? OurReference,
    [property: JsonPropertyName("orderReference")] string? OrderReference,
    [property: JsonPropertyName("lines")] IReadOnlyList<FikenInvoiceDraftLineRequest> Lines,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("bankAccountNumber")] string? BankAccountNumber,
    [property: JsonPropertyName("customerId")] long CustomerId,
    [property: JsonPropertyName("projectId")] long? ProjectId,
    [property: JsonPropertyName("uuid")] string? Uuid = null);

public sealed record FikenInvoiceDraftSummary(
    [property: JsonPropertyName("draftId")] long DraftId,
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("issueDate")] string? IssueDate,
    [property: JsonPropertyName("daysUntilDueDate")] int? DaysUntilDueDate,
    [property: JsonPropertyName("invoiceText")] string? InvoiceText,
    [property: JsonPropertyName("orderReference")] string? OrderReference,
    [property: JsonPropertyName("net")] long? Net,
    [property: JsonPropertyName("gross")] long? Gross,
    [property: JsonPropertyName("projectId")] long? ProjectId,
    [property: JsonPropertyName("customers")] IReadOnlyList<FikenContact>? Customers);

public sealed record FikenJournalEntryLine(
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("account")] string? Account,
    [property: JsonPropertyName("vatCode")] string? VatCode,
    [property: JsonPropertyName("projectId")] IReadOnlyList<long>? ProjectId);

public sealed record FikenJournalEntry(
    [property: JsonPropertyName("journalEntryId")] long JournalEntryId,
    [property: JsonPropertyName("transactionId")] long? TransactionId,
    [property: JsonPropertyName("transactionType")] string? TransactionType,
    [property: JsonPropertyName("journalEntryNumber")] int? JournalEntryNumber,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("lines")] IReadOnlyList<FikenJournalEntryLine>? Lines);

public sealed record CreatedResourceResult(long? Id, Uri? Location);
