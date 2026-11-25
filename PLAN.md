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
3. **Ikke MVA-pliktig:** Alle fakturalinjer fra kretsen opprettes med `vatType = "OUTSIDE"` (Fiken krever `OUTSIDE` for foretak som ikke er MVA-registrert).
4. **Fiken API-begrensning:** Fiken tillater maks **1 samtidig spørring** og maks **4 kall per sekund**. All kommunikasjon går gjennom `FikenRateLimitHandler` som serialiserer forespørsler med `SemaphoreSlim(1, 1)` og minst 260 ms mellomrom.
5. **Testforetak støttes:** Alt kan kobles mot et gratis testforetak i Fiken før man kjører mot kretsens ekte regnskap.

### UI og komponentbibliotek
- For Fase 1 ble det laget en enkel egen `SearchableSelect`-komponent uten ekstra pakkeavhengigheter for å komme raskt i mål.
- **Valgt bibliotek: MudBlazor (MIT).** Vurdert mot Radzen.Blazor og Microsoft Fluent UI Blazor. MudBlazor ble valgt for bredde, dokumentasjon og ren C#-API (`MudDataGrid`, `MudSelect`/`MudAutocomplete`, dialoger, snackbar, tema).
  - Appen bruker interaktivitet per side, så MudBlazor-providerne ligger i `Components/Shared/MudProviders.razor` og legges øverst i hver interaktive side som bruker MudBlazor (layouten er statisk).
  - Temaet bruker appens grønne farger. Bootstrap finnes fortsatt for eldre sider.
  - Fase 3-sidene er bygget med MudBlazor. `SearchableSelect` og håndskrevne tabeller i Fase 1 kan byttes ut gradvis.
- Grafer er ikke prioritert nå. Til årsmøtet lages presentasjonen fortsatt i Word/Google Docs, så eksporten må gi tall som er enkle å lime inn der.

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
  - Lagrede gruppe-til-kunde-koblinger i `%LocalAppData%\FikenHjelper\mappings.json` og automatisk lokal lagring av arbeidsutkast/deltakervalg per arrangement i `%LocalAppData%\FikenHjelper\drafts\`.
  - Mulighet til å opprette manglende kunde i Fiken direkte fra forhåndsvisningen.
  - Valgfritt ordrenummer (`orderReference`, sendes kun når utfylt) med varsel ved duplikat mot eksisterende fakturautkast i Fiken.
  - Sekvensiell opprettelse av `POST /companies/{slug}/invoices/drafts` med `vatType: "OUTSIDE"`.

---

## Fase 2 – Fleksibel CSV/XLSX-import for arrangementer *(Delvis implementert: Core/CSV ferdig, UI og XLSX gjenstår)*
**Mål:** Håndtere arrangementer som ikke bruker API-et, eller der man heller vil bruke eksportfilen fra Min speiding (Spond o.l. kan komme senere).

- **Implementert i Core (`FikenHjelper.Core/Import/`, branch `fase2-import`):**
  - `CsvTableReader`: parsing med **CsvHelper** (33.1.0). Egen kode bare for koding (UTF-8 med/uten BOM, ellers Windows-1252) og valg av skilletegn `;`/`,`/tab ut fra konsistent kolonneantall. Like overskrifter gjøres unike («E-post (2)»), og et anførselstegn som ikke lukkes gir feil i stedet for stille datatap.
  - `ImportProfileSuggester`: automatisk kolonnemapping for begge Min speiding-eksportene (individuell «… Deltaker - deltakere.csv» og gruppe-/fullrapport `projectParticipantsReportFull_*.csv`), valg av beste lagrede profil og forslag til arrangementsnavn fra filnavnet.
  - `ParticipantImportMapper`: produserer samme `MinSpeidingEventLoadResult` som API-et, så prising, kundematching, forhåndsvisning og utkast gjenbrukes uendret. Stabile deltakernøkler (medlemsnummer eller hash), Min speidings «-» regnes som tomt felt, avmeldt via Avmeldingsdato/statusverdier, Bekreftet (Ja/Nei eller tidspunkt)/Til stede/Innsjekket, «Antall» i fullrapporten tolkes som beløp (inneholder f.eks. «0,00»), avgift som «Lederkurs: 700.00» gir pris, en egen antall-kolonne («Antall plasser») gir flere deltakere (totalbeløp fordeles), oppsplitting av kategorier med ulike beløp. Svar på påmeldingsskjemaet («Generelt - …») varierer per arrangement og tas ikke med automatisk; bare kommentarfelt blir svar.
  - `JsonImportProfileStore`: navngitte importprofiler (kun kolonnenavn og regler, ingen persondata) i `%LocalAppData%\FikenHjelper\import-profiles.json`.
  - Tester med kun syntetiske data (`ImportTests.cs`, `ImportTestData.cs`).
- **Gjenstår (etter at `fase3-budsjett` med MudBlazor/ClosedXML er merget):**
  - `.xlsx`-leser via ClosedXML, med `RemoveInvalidRelationships` flyttet til en felles `XlsxSanitizer`.
  - UI på «Arrangementsfakturering»: kildevalg *Min speiding API | Fil (CSV/XLSX)*, opplasting (`MudFileUpload`), kolonnemapping, lagring av profiler, forhåndsvisning med advarsler og «Bruk deltakerne» → `ApplyLoadedEventAsync`. Arbeidsutkast lagres per arrangement med nøkkel `import:<arrangementsnavn>`.
  - Registrering av `IImportProfileStore` i `Program.cs`.

---

## Fase 3 – Budsjett- og prosjektrapporter (Intensjonsbudsjett) *(Under arbeid i branch `fase3-budsjett`)*
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

**Status for første versjon:**
- Fiken: `journalEntries` hentes per år (dato fra/til). Hver posteringslinje har `account` og `projectId`. Regnskap per prosjekt = −(sum av linjer på konto 3000–8799). 88xx/89xx holdes utenfor så årsavslutningen ikke nuller resultatet. *Verifisert mot ekte Fiken-data (24.11.2026): fortegnet stemmer, 2025 gir 50 254,29 på øret, og alle prosjektsummer stemmer med regnearket. Regelen ligger i `BudgetReportBuilder.TryGetResultAmount`/`PrimaryProjectId` og gjenbrukes av Fase 4.*
- Budsjett lagres i `budgets.json` i datamappen. En post har ett felt for beløp (inntekt positiv, kostnad negativ), Fiken-prosjekter per år og eventuelt manuelt regnskapstall (for år før prosjekter ble brukt i Fiken, f.eks. 2024).
- Import fra Google Sheets-arket «Sammenstilling av budsjett og regnskap» (.xlsx): oversiktsarket gir poster og beløp, arkene «Regnskap ÅÅÅÅ» gir prosjektkoblinger (navn matches mot Fiken-prosjekter). Pivotene fra Google Sheets har ugyldige relasjoner som ryddes bort før lesing.
- Sider: `/budsjett` (flere år side om side, utvidbare rader per prosjekt, kontroll per år) og `/budsjett/oppsett` (redigering, kopier fra forrige år, import).
- Eksport: `/budsjett/eksport?year=ÅÅÅÅ&years=N` med fanene *Budsjettoversikt*, *Prosjekter* og *Kontroll*.
- Kjent: importen matcher prosjekter på navn, så omdøpte prosjekter (f.eks. «Kretsbannerkonkurranse» → «Kretskonkurranse» i Fiken) må kobles manuelt. Importen varsler om dette.
- Gjenstår: drill-down til enkeltbilag, eventuelt kopiering av tabellen til Word/Docs, og på sikt direkte kobling mot Google Sheets-arket.

**Neste forbedringer (fra brukertest og verifisering 24.11.2026):**
- **Prosjekt fordelt på flere budsjettposter:** Et prosjekt skal kunne kobles til flere poster, med en fordelingsnøkkel (f.eks. prosent eller faste beløp) som deler prosjektets resultat mellom postene. Summen av fordelingen må gå opp i 100 %. Kontrollen for «prosjekt koblet til flere poster» blir da en kontroll av at nøkkelen er satt og går opp. Gjelder rapport, eksport og import.
- **Mer oversiktlig kobling i «Rediger budsjett»:** Med mange prosjekter er nedtrekkslisten tung å bruke. Vurder f.eks. søk/filter, skjule avsluttede og allerede koblede prosjekter, eller en egen visning med prosjekter i én kolonne og budsjettpost i en annen.
- **Foreslå prosjekter etter navn:** Prosjekter som matcher eller nesten matcher budsjettpostens navn, og som ikke er koblet ennå, legges øverst i listen. Samme likhetsmatching brukes i importen: når et navn ikke finnes eksakt (f.eks. «Kretsbannerkonkurranse» → «Kretskonkurranse»), foreslås nærmeste prosjekt i stedet for bare en advarsel.

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

## Fase 5 – .NET Aspire-støtte (Orkestrering og telemetri) *(Implementert)*
**Mål:** Enkel lokal oppstart med én kommando, samlet logging/tracing av API-kall mot Fiken og Min speiding, og felles helsesjekker når løsningen vokser med både `Web` og `Mcp`.

- **`FikenHjelper.AppHost`:**
  - Orkestrerer `FikenHjelper.Web` (og senere `FikenHjelper.Mcp` ved HTTP/SSE-transport) slik at alt kan startes samlet med `dotnet run --project src/FikenHjelper.AppHost`.
  - Gir **Aspire Dashboard** lokalt med sanntidslogger, strukturerte traces og oversikt over alle HTTP-kall mot `api.fiken.no` og `min.speiding.no`.
  - Håndterer hemmeligheter trygt med Aspire-parametere (`secret: true`) koblet mot lokal `user-secrets` (f.eks. `Fiken:ApiToken`), slik at hemmeligheter aldri havner i Git.
- **`FikenHjelper.ServiceDefaults`:**
  - Legger til standard **OpenTelemetry** (tracing, metrikker og logger), helsesjekk-endepunkter (`/health` og `/alive`) og tjenesteoppdagelse (`AddServiceDiscovery`).
  - **Viktig om Fiken-klienten:** `FikenClient` beholder sin egen sekvensielle `FikenRateLimitHandler` (maks 1 samtidig kall / 260 ms mellomrom) og bruker *ikke* standard `AddStandardResilienceHandler` med parallelle/aggressive retries som kan bryte Fikens rate-limit-regler.
- **Status (november 2026):**
  - Aspire **17.0.0** (`Aspire.Hosting.AppHost` + `Aspire.Hosting.Dotnet`, vanlig `Microsoft.NET.Sdk`) – ingen workload/templates nødvendig på Windows eller Linux; DCP, dashboard og Aspire-CLI hentes som NuGet-pakker.
  - AppHost legger til web-appen med `AddDotnetProject` og launch-profil `https`, med `IsProxied = false` slik at appen lytter direkte på 5066/7193 og OAuth-redirect-URI-ene er uendret. AppHost har `https`- og `http`-profil (sistnevnte for Linux uten klarert dev-sertifikat).
  - Fiken-hemmeligheter (`fiken-client-id`, `fiken-client-secret`, `fiken-api-token`) er *valgfrie* secret-parametere – de kobles bare til når de finnes i AppHost sine user-secrets.
  - ServiceDefaults refererer ikke `Microsoft.Extensions.Http.Resilience`; `ConfigureHttpClientDefaults` legger kun på tjenesteoppdagelse. Fiken-registreringen er flyttet til `AddFikenApiClient()` i Core (gjenbrukes av Fase 4).
  - Aspire slår av query-redigering i telemetri; ServiceDefaults redigerer likevel alltid `url.query` på innkommende forespørsler (OAuth `code`/`state`).
  - Tester (`ServiceDefaultsFikenTests`, `ServiceDefaultsTelemetryTests`): med ServiceDefaults aktivert gir 503 nøyaktig ett kall, 429 gir kun FikenRateLimitHandler sine egne retries, parallelle kall serialiseres med ≥260 ms mellomrom, og query-strenger lekker ikke til traces.
  - Gjenstår: verifisere Fiken-kall i dashboardet på maskin med Fiken-innlogging; legge til `FikenHjelper.Mcp` i AppHost når Fase 4 får HTTP-transport.

---

## Fase 6 – Idébank for videre utvidelser
- **Utlegg og reiseregninger fra frivillige:** Tolke kvitteringer/skjemaer via AI/MCP og klargjøre kjøpsutkast per prosjekt.
- **Vipps- og bankavstemming:** Matche oppgjørsfiler fra Vipps mot deltakerlister fra Min speiding og åpne salg i Fiken.
- **Kvalitetssjekk før styremøte og årsoppgjør:** Sjekkliste over forfalte fakturaer, bilag uten prosjekt og åpne poster.
- **Tilskuddsgrunnlag:** Ferdige oppsummeringer for Frifond, momskompensasjon og fylkeskommunale tilskudd.
