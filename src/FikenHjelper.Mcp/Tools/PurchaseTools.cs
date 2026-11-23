using System.ComponentModel;
using System.Globalization;
using System.Net;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Purchasing;
using FikenHjelper.Mcp.Safety;
using FikenHjelper.Mcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FikenHjelper.Mcp.Tools;

public sealed class PurchaseLineArgument
{
    [Description("Linjetekst (maks 200 tegn), f.eks. hva som er kjøpt.")]
    public string Text { get; set; } = string.Empty;

    [Description("Kostnadskonto fra kontoplanen, f.eks. \"6800\".")]
    public string Account { get; set; } = string.Empty;

    [Description("Beløp i kroner inkl. eventuell mva (brutto), maks to desimaler, f.eks. 1249.50.")]
    public decimal AmountKr { get; set; }

    [Description("Fiken-prosjekt (projectId) for linjen, eller utelat for ingen prosjekt.")]
    public long? ProjectId { get; set; }
}

[McpServerToolType]
public sealed class PurchaseTools(
    FikenContextService context,
    FikenLookups lookups,
    IFikenPurchasingClient purchasing,
    IBudgetStore budgetStore,
    WriteGuard writeGuard,
    PendingDraftStore pendingDrafts,
    McpSettings mcpSettings,
    TimeProvider time)
{
    [McpServerTool(Name = "suggest_accounting_and_projects", Title = "Forslag til konto og prosjekt", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Slår opp leverandørens tidligere kjøp i Fiken og foreslår kostnadskonto og prosjekt (rangert, med eksempler på linjetekster), sammen med aktive prosjekter og budsjettposter for året. Oppgi ehfDocumentId, eller leverandør via supplierContactId/orgNumber/supplierName.")]
    public Task<object> SuggestAccountingAndProjects(
        [Description("EHF-dokument som skal behandles (henter leverandør og beskrivelse derfra).")] long? ehfDocumentId = null,
        [Description("Leverandørens contactId i Fiken.")] long? supplierContactId = null,
        [Description("Leverandørens organisasjonsnummer.")] string? orgNumber = null,
        [Description("Leverandørens navn (brukes hvis id/orgnr mangler).")] string? supplierName = null,
        [Description("Kort beskrivelse av hva fakturaen gjelder (forbedrer rangeringen).")] string? description = null,
        [Description("Antall år bakover med kjøpshistorikk (1-5).")] int historyYears = 3,
        [Description("Ta med hele listen over kostnadskontoer (4000-7999).")] bool includeChartOfAccounts = false,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        var slug = await context.GetCompanySlugAsync(ct);
        var today = ToolSupport.Today(time);
        var notes = new List<string>();

        FikenEhfDocument? ehf = null;
        if (ehfDocumentId is { } ehfId)
        {
            ehf = await purchasing.GetEhfDocumentAsync(slug, ehfId, ct);
            supplierContactId ??= ehf.SupplierContactId;
            orgNumber ??= ehf.SupplierOrganizationNumber;
            supplierName ??= ehf.SupplierName;
            description ??= string.Join(" ", new[] { ehf.Description }.Concat(ehf.Lines?.Select(l => l.Description) ?? []).Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        FikenContact? supplier = null;
        IReadOnlyList<FikenContact> candidates = [];
        if (supplierContactId is { } contactId)
        {
            supplier = await purchasing.GetContactAsync(slug, contactId, ct);
        }
        else
        {
            var digits = new string((orgNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length == 9)
            {
                candidates = await purchasing.GetSuppliersAsync(slug, organizationNumber: digits, ct: ct);
            }

            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(supplierName))
            {
                var all = await lookups.GetSuppliersAsync(slug, ct);
                candidates = all.Where(s => string.Equals(s.Name.Trim(), supplierName.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (candidates.Count == 0)
                {
                    candidates = all.Where(s => s.Name.Contains(supplierName.Trim(), StringComparison.CurrentCultureIgnoreCase)).Take(10).ToList();
                }
            }

            if (candidates.Count == 1)
            {
                supplier = candidates[0];
            }
            else if (candidates.Count > 1)
            {
                notes.Add("Flere mulige leverandører. Velg riktig contactId og kall verktøyet på nytt med supplierContactId.");
            }
            else
            {
                notes.Add("Fant ikke leverandøren i Fiken. Utkastet kan opprettes uten leverandør, og leverandøren velges i Fiken.");
            }
        }

        var years = Math.Clamp(historyYears, 1, 5);
        var history = supplier is null
            ? []
            : await purchasing.GetPurchasesAsync(slug, contactId: supplier.ContactId, dateFrom: today.AddYears(-years), ct: ct);
        var accounts = await lookups.GetAccountsAsync(slug, FikenLookups.ExpenseRange, ct);
        var projects = await lookups.GetProjectsAsync(slug, ct);
        var budget = await budgetStore.LoadAsync(ct);
        var budgetYear = budget.FindYear(today.Year);

        var suggestion = PurchaseSuggestionBuilder.Build(history, accounts, projects, budgetYear, description, today);
        var budgetLinesByProject = (budgetYear?.Lines ?? [])
            .SelectMany(l => l.ProjectIds.Select(id => (id, l.Name)))
            .GroupBy(x => x.id)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().ToList());

        return new
        {
            supplier = supplier is null ? null : new { supplier.ContactId, supplier.Name, supplier.OrganizationNumber },
            supplierCandidates = supplier is null && candidates.Count > 1
                ? candidates.Select(c => new { c.ContactId, c.Name, c.OrganizationNumber }).ToList()
                : null,
            document = ehf is null ? null : InboxTools.ToEhfSummary(ehf),
            historyYears = years,
            suggestion.PurchasesAnalysed,
            accountCandidates = suggestion.Accounts,
            projectCandidates = suggestion.Projects,
            activeProjects = projects
                .Where(p => !p.Completed)
                .OrderBy(p => p.Number, StringComparer.Ordinal)
                .Select(p => new
                {
                    p.ProjectId,
                    p.Number,
                    p.Name,
                    budgetLines = budgetLinesByProject.GetValueOrDefault(p.ProjectId)
                })
                .ToList(),
            expenseAccounts = includeChartOfAccounts || suggestion.Accounts.Count == 0
                ? accounts.Select(a => new { a.Code, a.Name }).ToList()
                : null,
            notes = notes.Concat(suggestion.Notes).ToList()
        };
    });

    [McpServerTool(Name = "list_purchase_drafts", Title = "Kjøpsutkast", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lister eksisterende kjøpsutkast i Fiken (ikke bokført ennå).")]
    public Task<object> ListPurchaseDrafts(
        [Description("Maks antall (1-200).")] int max = 50,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        var slug = await context.GetCompanySlugAsync(ct);
        var drafts = await purchasing.GetPurchaseDraftsAsync(slug, ct);
        return new
        {
            total = drafts.Count,
            drafts = drafts.Take(Math.Clamp(max, 1, 200)).Select(d => new
            {
                d.DraftId,
                supplier = d.Contact?.Name,
                d.InvoiceNumber,
                d.InvoiceIssueDate,
                d.DueDate,
                grossKr = d.GrossAmount / 100m,
                attachments = d.Attachments?.Count ?? 0,
                lines = d.Lines?.Select(l => new { l.Text, l.Account, grossKr = ToolSupport.Kr(l.Gross), project = l.Project?.Name }).ToList()
            }).ToList()
        };
    });

    [McpServerTool(Name = "preview_purchase_draft", Title = "Forhåndsvis kjøpsutkast", ReadOnly = true, Idempotent = false, OpenWorld = true)]
    [Description("Validerer et kjøpsutkast og viser nøyaktig hva som vil bli opprettet (uten å skrive noe til Fiken). Gir et confirmationToken (gyldig i 15 min) når alt er gyldig. Vis oppsummeringen og advarslene til brukeren, og kall create_purchase_draft bare etter at brukeren uttrykkelig har sagt ja.")]
    public Task<object> PreviewPurchaseDraft(
        [Description("Linjer med tekst, kostnadskonto, beløp i kr (brutto) og eventuelt prosjekt per linje.")] PurchaseLineArgument[] lines,
        [Description("Innboksdokument som skal legges ved (fra list_inbox_documents).")] long? inboxDocumentId = null,
        [Description("EHF-dokument som skal legges ved (fra list_ehf_documents). Leverandør, fakturanr., datoer og KID hentes derfra hvis de ikke oppgis.")] long? ehfDocumentId = null,
        [Description("Leverandørens contactId i Fiken. Utelat hvis leverandøren ikke finnes (velges da i Fiken).")] long? supplierContactId = null,
        [Description("Leverandørens fakturanummer.")] string? invoiceNumber = null,
        [Description("Fakturadato (ÅÅÅÅ-MM-DD).")] string? issueDate = null,
        [Description("Forfallsdato (ÅÅÅÅ-MM-DD).")] string? dueDate = null,
        [Description("KID-nummer.")] string? kid = null,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        if (inboxDocumentId.HasValue && ehfDocumentId.HasValue)
        {
            throw new ArgumentException("Oppgi bare én av inboxDocumentId og ehfDocumentId.");
        }

        var slug = await context.GetCompanySlugAsync(ct);
        var today = ToolSupport.Today(time);
        var source = inboxDocumentId is { } inboxId ? PurchaseDocumentSource.Inbox(inboxId)
            : ehfDocumentId is { } ehfId ? PurchaseDocumentSource.Ehf(ehfId)
            : null;

        var ehf = ehfDocumentId is { } e ? await purchasing.GetEhfDocumentAsync(slug, e, ct) : null;
        var inbox = inboxDocumentId is { } i ? await purchasing.GetInboxDocumentAsync(slug, i, ct) : null;
        var contactId = supplierContactId ?? ehf?.SupplierContactId;
        FikenContact? supplier = null;
        if (contactId is { } cid)
        {
            try
            {
                supplier = await purchasing.GetContactAsync(slug, cid, ct);
            }
            catch (FikenApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                supplier = null;
            }
        }

        var supplierPurchases = supplier is null
            ? []
            : await purchasing.GetPurchasesAsync(slug, contactId: supplier.ContactId, dateFrom: today.AddYears(-2), ct: ct);
        var accounts = await lookups.GetAccountsAsync(slug, FikenLookups.AllAccountsRange, ct);
        var projects = await lookups.GetProjectsAsync(slug, ct);
        var drafts = await purchasing.GetPurchaseDraftsAsync(slug, ct);

        var input = new PurchaseDraftInput(
            source,
            supplierContactId,
            invoiceNumber,
            ToolSupport.ParseDate(issueDate, nameof(issueDate)),
            ToolSupport.ParseDate(dueDate, nameof(dueDate)),
            kid,
            (lines ?? []).Select(l => new PurchaseDraftLineInput(l.Text, l.Account, l.AmountKr, l.ProjectId)).ToList());
        var validation = PurchaseDraftValidator.Validate(
            input,
            new PurchaseDraftContext(accounts, projects, drafts, supplierPurchases, supplier, ehf, inbox, mcpSettings.PurchaseVatType, today));

        var company = await context.GetActiveCompanyAsync(ct);
        var decision = WriteGuard.Decide(company, mcpSettings.AllowedDraftCompanies);
        var accountNames = accounts.GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First().Name);
        var projectNames = projects.GroupBy(p => p.ProjectId).ToDictionary(g => g.Key, g => g.First().Name);

        var summaryLines = (validation.Request?.Lines ?? []).Select(l => new
        {
            l.Text,
            l.Account,
            accountName = accountNames.GetValueOrDefault(l.Account),
            amountKr = l.Gross / 100m,
            l.ProjectId,
            projectName = l.ProjectId is { } pid ? projectNames.GetValueOrDefault(pid) : null,
            l.VatType
        }).ToList();

        var summary = validation.Request is null
            ? null
            : BuildSummary(company, supplier, validation.Request, source, validation.TotalKr);

        string? token = null;
        DateTimeOffset? expiresAt = null;
        if (validation.IsValid && decision.Allowed)
        {
            (token, expiresAt) = pendingDrafts.Add(slug, validation.Request!, source, summary!);
        }

        return new
        {
            valid = validation.IsValid,
            writeAllowed = decision.Allowed,
            writePolicy = decision.Reason,
            confirmationToken = token,
            expiresAt,
            company = new { company.Slug, company.Name, company.IsTestCompany },
            supplier = supplier is null ? null : new { supplier.ContactId, supplier.Name },
            invoiceNumber = validation.Request?.InvoiceNumber,
            issueDate = validation.Request?.InvoiceIssueDate,
            dueDate = validation.Request?.DueDate,
            kid = validation.Request?.Kid,
            attachment = source?.ToString(),
            paid = false,
            lines = summaryLines,
            totalKr = validation.TotalKr,
            errors = validation.Errors,
            warnings = validation.Warnings,
            summary,
            nextStep = token is not null
                ? "Vis oppsummeringen og advarslene for brukeren. Kall create_purchase_draft med confirmationToken bare etter at brukeren har sagt ja."
                : validation.IsValid
                    ? "Utkastet er gyldig, men kan ikke opprettes i dette foretaket (se writePolicy)."
                    : "Rett feilene og kall preview_purchase_draft på nytt."
        };
    });

    [McpServerTool(Name = "create_purchase_draft", Title = "Opprett kjøpsutkast", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Oppretter kjøpsutkastet som ble forhåndsvist med preview_purchase_draft, og legger ved bilaget. Krever confirmationToken fra forhåndsvisningen og at brukeren har bekreftet. Utkastet blir IKKE bokført eller betalt.")]
    public Task<object> CreatePurchaseDraft(
        [Description("Token fra preview_purchase_draft (kan brukes én gang, gyldig i 15 min).")] string confirmationToken,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        await writeGuard.EnsureAllowedAsync(ct);
        var slug = await context.GetCompanySlugAsync(ct);
        if (!pendingDrafts.TryTake(confirmationToken, out var pending) || pending is null)
        {
            throw new McpException("Ukjent, brukt eller utløpt confirmationToken. Kall preview_purchase_draft på nytt og få ny bekreftelse fra brukeren.");
        }

        if (!string.Equals(pending.CompanySlug, slug, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("Foretaket er endret siden forhåndsvisningen. Kall preview_purchase_draft på nytt.");
        }

        var created = await purchasing.CreatePurchaseDraftAsync(slug, pending.Request, ct);
        string attachmentStatus;
        if (pending.Source is null)
        {
            attachmentStatus = "Ingen vedlegg valgt.";
        }
        else if (created.Id is not { } draftId)
        {
            attachmentStatus = $"Utkastet ble opprettet, men Fiken returnerte ingen id, så {pending.Source} ble ikke lagt ved. Legg ved bilaget i Fiken.";
        }
        else
        {
            try
            {
                await purchasing.AttachDocumentToPurchaseDraftAsync(slug, draftId, pending.Source, ct);
                attachmentStatus = $"{pending.Source} er lagt ved og markert som brukt i Fiken.";
            }
            catch (FikenApiException ex)
            {
                attachmentStatus = $"Utkastet ble opprettet, men vedlegget feilet: {ex.Message}. Prøv attach_document_to_purchase_draft, eller legg ved i Fiken.";
            }
        }

        return new
        {
            created = true,
            draftId = created.Id,
            attachment = attachmentStatus,
            pending.Summary,
            reminder = "Utkastet er ikke bokført eller betalt. Kontroller det i Fiken (Kjøp → Utkast) og bokfør derfra."
        };
    });

    [McpServerTool(Name = "attach_document_to_purchase_draft", Title = "Legg ved bilag på kjøpsutkast", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Legger et innboks- eller EHF-dokument ved et eksisterende kjøpsutkast (f.eks. hvis vedlegget feilet i create_purchase_draft). Dokumentet markeres som brukt i Fiken. Spør brukeren først.")]
    public Task<object> AttachDocumentToPurchaseDraft(
        [Description("Kjøpsutkastets draftId.")] long draftId,
        [Description("Innboksdokument som skal legges ved.")] long? inboxDocumentId = null,
        [Description("EHF-dokument som skal legges ved.")] long? ehfDocumentId = null,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        await writeGuard.EnsureAllowedAsync(ct);
        var source = new PurchaseDocumentSource(inboxDocumentId, ehfDocumentId);
        if (!source.IsValid)
        {
            throw new ArgumentException("Oppgi nøyaktig én av inboxDocumentId og ehfDocumentId.");
        }

        var slug = await context.GetCompanySlugAsync(ct);
        var drafts = await purchasing.GetPurchaseDraftsAsync(slug, ct);
        if (drafts.All(d => d.DraftId != draftId))
        {
            throw new McpException($"Fant ikke kjøpsutkast {draftId}. Vedlegg kan bare legges på kjøpsutkast.");
        }

        await purchasing.AttachDocumentToPurchaseDraftAsync(slug, draftId, source, ct);
        return new { attached = true, draftId, document = source.ToString() };
    });

    private static string BuildSummary(
        ActiveCompany company,
        FikenContact? supplier,
        FikenPurchaseDraftRequest request,
        PurchaseDocumentSource? source,
        decimal totalKr)
    {
        var nb = CultureInfo.GetCultureInfo("nb-NO");
        return $"Kjøpsutkast i «{company.Name ?? company.Slug}»: {supplier?.Name ?? "uten leverandør"}, " +
               $"fakturanr. {request.InvoiceNumber ?? "–"}, fakturadato {request.InvoiceIssueDate ?? "–"}, forfall {request.DueDate ?? "–"}, " +
               $"{request.Lines.Count} linje(r), totalt {totalKr.ToString("N2", nb)} kr, vedlegg: {source?.ToString() ?? "ingen"}. Ikke betalt, ikke bokført.";
    }
}
