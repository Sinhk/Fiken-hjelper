# Fiken-hjelper – Plan og veikart

Verktøysamling i **.NET 10** for regnskapsfører/kasserer i **Romsdal og Nordmøre krets (Norges speiderforbund)**.

## Arkitektur og grunnprinsipper

```
FikenHjelper.slnx
├── src/FikenHjelper.Core             # Fiken API v2-klient, Min speiding-klient, fakturamotor, senere import/budsjett
├── src/FikenHjelper.Web              # Blazor Web App (Interactive Server per side) – UI for import, forhåndsvisning og rapporter
├── src/FikenHjelper.Mcp              # (Fase 4) C# MCP-server for AI-assistert bilagsbehandling og analyse
├── src/FikenHjelper.AppHost          # (Fase 5) .NET Aspire AppHost for felles oppstart og Aspire Dashboard
├── src/FikenHjelper.ServiceDefaults  # (Fase 5) Felles OpenTelemetry, helsesjekker og tjenesteoppdagelse
└── tests/FikenHjelper.Tests          # xUnit-tester for parsing, kundematching, beløp/øre, rate-limiting
```

### Faste sikkerhetsregler
1. **Alltid utkast først:** Verktøyene oppretter kun *fakturautkast* og *kjøpsutkast* i Fiken. Selve utsendingen og bankutbetalingen skjer manuelt etter kontroll i Fiken.
2. **Ingen hemmeligheter i Git:** API-nøkler (Fiken og Min speiding) lagres lokalt via `dotnet user-secrets` eller i `%LocalAppData%\FikenHjelper\`, aldri i kode eller appsettings som sjekkes inn.
3. **Ikke MVA-pliktig:** Alle fakturalinjer fra kretsen opprettes med `vatType = "NONE"`.
4. **Fiken API-begrensning:** Fiken tillater maks **1 samtidig spørring** og maks **4 kall per sekund**. All kommunikasjon går gjennom `FikenRateLimitHandler` som serialiserer forespørsler med `SemaphoreSlim(1, 1)` og minst 260 ms mellomrom.
5. **Testforetak støttes:** Alt kan kobles mot et gratis testforetak i Fiken før man kjører mot kretsens ekte regnskap.

### UI og komponentbibliotek
- For Fase 1 ble det laget en enkel egen `SearchableSelect`-komponent uten ekstra pakkeavhengigheter for å komme raskt i mål.
- Før videre UI-utbygging (Fase 2/3 med kolonnemapping, større tabeller, filtre, dialoger og budsjettrapporter) bør løsningen ta i bruk et åpent / gratis-for-frivillige Blazor-komponentbibliotek (**MIT-lisens**, uten lisensnøkler) i stedet for å bygge og vedlikeholde egne komplekse UI-komponenter:
  - **MudBlazor** (MIT) – bredt utvalg komponenter (`MudAutocomplete`, `MudDataGrid`, dialoger, tema) i ren C#/Blazor.
  - **Radzen.Blazor** (MIT) – sterk på søkbare nedtrekkslister (`RadzenDropDown`), avanserte datatabeller (`RadzenDataGrid`) og grafer til budsjettrapporter.
  - **Microsoft Fluent UI Blazor** (MIT) – offisielt Microsoft-bibliotek med rent grensesnitt (`FluentAutocomplete`, `FluentDataGrid`).
- Når bibliotek er valgt, erstattes dagens `SearchableSelect` og håndskrevne tabeller/skjemaer gradvis.

---

## Fase 1 – Arrangementsfakturering via Min speiding API *(Implementert)*
**Mål:** Hente påmeldte fra et arrangement i `min.speiding.no`, beregne priser per rolle/kategori og opprette fakturautkast i Fiken.

- **Kilde (`min.speiding.no/api`) og autentisering:**
  - Separate API-nøkler per Min speiding-endepunkt (`GET /project/get/participants` + valgfri `GET /project/get/questions`) med HTTP Basic auth (`projectId:apiKey`), med lokal lagring per arrangement.
  - Fiken **OAuth2 + PKCE**-innlogging (med personlig API-nøkkel som fallback).
  - Robust JSON-parsing som håndterer PHP-særegenheter (tomme lister `[]` i stedet for `{}` på objekter/oppslag, streng/tall/liste på spørsmålssvar, ekstra norske felter via `JsonExtensionData`).
- **Regler, prising og filtrering:**
  - Samlefaktura per speidergruppe (standard) – valgfritt med én linje per deltaker eller sammendrag per priskategori – eller enkeltfaktura per deltaker.
  - Auto-forslag av pris fra `labels.project_fee` (f.eks. *Speider 450 kr*, *Leder 250 kr*) med redigerbare priser per kategori og overstyring/ekskludering per enkeltperson.
  - Filter for avmeldte, gratis (`0 kr`), bekreftede og oppmøtte, samt mulighet til å ekskludere hele priskategorier (f.eks. `Stab`) uten tomme fakturautkast.
  - Valg av speidergruppe per deltaker dersom en deltaker er medlem i flere grupper (eller ved manuell overstyring).
  - Avkrysning for **«Dekkes av kretsen»** per deltaker: trekkes ut av gruppefakturaene og vises i egen oppsummeringstabell og kopierbar tekstoversikt.
- **Kundematching, søk og Fiken-utkast:**
  - Søkbare nedtrekkslister (`SearchableSelect`) for Fiken-kunde, prosjekt og inntektskonto.
  - Automatisk matching mot kunder i Fiken (lagrede koblinger → eksakt navn → normalisert gruppenavn → e-post → forslag).
  - Lagrede gruppe-til-kunde-koblinger i `%LocalAppData%\FikenHjelper\mappings.json`.
  - Mulighet til å opprette manglende kunde i Fiken direkte fra forhåndsvisningen.
  - Varsel ved duplikat `orderReference` mot eksisterende fakturautkast i Fiken.
  - Sekvensiell opprettelse av `POST /companies/{slug}/invoices/drafts` med `vatType: "NONE"`.

---

## Fase 2 – Fleksibel CSV/XLSX-import for arrangementer
**Mål:** Håndtere arrangementer som ikke bruker API-et, eksterne påmeldingssystemer, eller påmeldinger med egne spesialkolonner i eksporten.

- **Opplasting og gjenkjenning:**
  - Støtte for `.csv` (semikolon/komma, UTF-8/Windows-1252) og `.xlsx` (f.eks. via `ClosedXML` / `CsvHelper`).
- **Interaktiv kolonnemapping i Blazor UI:**
  - Brukeren laster opp filen og kobler kolonner til feltene *Navn*, *E-post*, *Speidergruppe*, *Priskategori/Rolle*, *Beløp*, *Status (avmeldt)* og *Valgfri kommentar*.
  - Støtte for å lagre navngitte **importprofiler** lokalt (f.eks. «Standard Min speiding gruppeliste», «Kurs-eksport med matvalg»).
- **Gjenbruk av kjerne:**
  - Parseren produserer samme `MinSpeidingEventLoadResult` / `EventParticipant`-modell, slik at hele prisingen, kundematchingen, forhåndsvisningen og Fiken-utkastopprettelsen gjenbrukes direkte.

---

## Fase 3 – Budsjett- og prosjektrapporter (Intensjonsbudsjett)
**Mål:** Sammenligne kretsens intensjonsbudsjett/resultatbudsjett mot faktiske bokførte tall i Fiken, der én budsjettpost kan være koblet til ett eller flere Fiken-prosjekter.

- **Budsjett-oppsett (lokal konfigurasjon + UI-redigering):**
  - Årsbudsjett med poster/kategorier (navn, budsjettert inntekt, budsjettert kostnad).
  - Hver budsjettpost kobles til **ett eller flere prosjekter** (`projectId` / prosjektnummer) i Fiken, samt valgfritt kontointervall (f.eks. 3xxx inntekter, 4xxx–7xxx kostnader).
- **Datainnhenting fra Fiken API v2:**
  - Hente `projects`, `accounts`, `transactions` / `journalEntries` (og åpne salg/kjøp) for valgt periode/år.
  - Aggregere faktisk regnskap per prosjekt og konto tilbake til hver budsjettpost.
- **Kontroll mot feilføringer:**
  - Egen varselliste for **ufordelte bilag** (bokføringer på relevante resultatkonti uten prosjekt) og **prosjekter i Fiken som ikke er koblet til noen budsjettpost** – så ingenting faller mellom stolene før styremøte eller kretsting.
- **Visning og eksport:**
  - Oversiktlig rapporttabell i Blazor UI med budsjett, hittil i år, avvik i kr og %, samt drill-down ned til enkeltbilag per budsjettpost.
  - Eksport til ferdig formatert **Excel-fil (`.xlsx`)** med tre faner: *Budsjettoversikt*, *Kontroll (ufordelte/ukoblede)* og *Bilagsdetaljer*.

---

## Fase 4 – MCP-server for AI-chat (`FikenHjelper.Mcp`)
**Mål:** Gjøre daglige skjønnsoppgaver mye raskere via AI-chat (Claude Desktop, VS Code, Antigravity m.fl.) basert på den offisielle C#-SDK-en `ModelContextProtocol`.

- **Hovedflyt: Innkommende fakturaer (velge konto + fordele på prosjekt):**
  - `list_inbox_documents` / `list_ehf_documents`: Hente ubehandlede bilag fra Fikens innboks og EHF-mottak.
  - `suggest_accounting_and_projects`: Slå opp leverandørens tidligere kjøp i Fiken (`GET /purchases`), aktiv kontoplan og aktive prosjekter, slik at AI-en kan foreslå riktig kostnadskonto og eventuell fordeling på flere prosjekter.
  - `create_purchase_draft`: Opprette `POST /purchases/drafts` med vedlegg koblet fra innboksen og tydelig prosjektfordeling per linje (alltid som **utkast**, aldri direkte bokført eller betalt).
- **Andre nyttige MCP-verktøy:**
  - `get_budget_status` / `explain_project_variance`: Hente tall fra Fase 3 slik at AI-en kan forklare avvik og skrive korte oppsummeringer til styremøtet.
  - `find_accounting_anomalies`: Finne bilag uten prosjektkode, ubetalte fakturaer eller manglende vedlegg.

---

## Fase 5 – .NET Aspire-støtte (Orkestrering og telemetri)
**Mål:** Enkel lokal oppstart med én kommando, samlet logging/tracing av API-kall mot Fiken og Min speiding, og felles helsesjekker når løsningen vokser med både `Web` og `Mcp`.

- **`FikenHjelper.AppHost`:**
  - Orkestrerer `FikenHjelper.Web` (og senere `FikenHjelper.Mcp` ved HTTP/SSE-transport) slik at alt kan startes samlet med `dotnet run --project src/FikenHjelper.AppHost`.
  - Gir **Aspire Dashboard** lokalt med sanntidslogger, strukturerte traces og oversikt over alle HTTP-kall mot `api.fiken.no` og `min.speiding.no`.
  - Håndterer hemmeligheter trygt med Aspire-parametere (`secret: true`) koblet mot lokal `user-secrets` (f.eks. `Fiken:ApiToken`), slik at hemmeligheter aldri havner i Git.
- **`FikenHjelper.ServiceDefaults`:**
  - Legger til standard **OpenTelemetry** (tracing, metrikker og logger), helsesjekk-endepunkter (`/health` og `/alive`) og tjenesteoppdagelse (`AddServiceDiscovery`).
  - **Viktig om Fiken-klienten:** `FikenClient` beholder sin egen sekvensielle `FikenRateLimitHandler` (maks 1 samtidig kall / 260 ms mellomrom) og bruker *ikke* standard `AddStandardResilienceHandler` med parallelle/aggressive retries som kan bryte Fikens rate-limit-regler.

---

## Fase 6 – Idébank for videre utvidelser
- **Utlegg og reiseregninger fra frivillige:** Tolke kvitteringer/skjemaer via AI/MCP og klargjøre kjøpsutkast per prosjekt.
- **Vipps- og bankavstemming:** Matche oppgjørsfiler fra Vipps mot deltakerlister fra Min speiding og åpne salg i Fiken.
- **Kvalitetssjekk før styremøte og årsoppgjør:** Sjekkliste over forfalte fakturaer, bilag uten prosjekt og åpne poster.
- **Tilskuddsgrunnlag:** Ferdige oppsummeringer for Frifond, momskompensasjon og fylkeskommunale tilskudd.
