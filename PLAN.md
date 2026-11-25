# Fiken-hjelper – Plan og veikart

Verktøysamling i **.NET 11** for regnskapsfører/kasserer i **Romsdal og Nordmøre krets (Norges speiderforbund)**.

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

## Fase 4 – MCP-server for AI-chat (`FikenHjelper.Mcp`) *(Implementert i branch `fase4-mcp`, ikke testet mot Fiken ennå)*
**Mål:** Gjøre daglige skjønnsoppgaver mye raskere via AI-chat (Claude Desktop, VS Code, Antigravity m.fl.) basert på den offisielle C#-SDK-en `ModelContextProtocol`.

- **Hovedflyt: Innkommende fakturaer (velge konto + fordele på prosjekt):**
  - `list_inbox_documents` / `list_ehf_documents`: Hente ubehandlede bilag fra Fikens innboks og EHF-mottak.
  - `suggest_accounting_and_projects`: Slå opp leverandørens tidligere kjøp i Fiken (`GET /purchases`), aktiv kontoplan og aktive prosjekter, slik at AI-en kan foreslå riktig kostnadskonto og eventuell fordeling på flere prosjekter.
  - `create_purchase_draft`: Opprette `POST /purchases/drafts` med vedlegg koblet fra innboksen og tydelig prosjektfordeling per linje (alltid som **utkast**, aldri direkte bokført eller betalt).
- **Andre nyttige MCP-verktøy:**
  - `get_budget_status` / `explain_project_variance`: Hente tall fra Fase 3 slik at AI-en kan forklare avvik og skrive korte oppsummeringer til styremøtet.
  - `find_accounting_anomalies`: Finne bilag uten prosjektkode, ubetalte fakturaer eller manglende vedlegg.

**Status for første versjon:**
- **Serveren:**
  - Konsollapp over stdio med `ModelContextProtocol` 2.4.0.
  - 15 verktøy og 2 prompter (`behandle_innboks`, `styremote_oppsummering`).
  - Bruker samme `config.json`, innlogging og user-secrets som Web. Oppsett av klientene står i README.
- **Fiken-klienten:** `IFikenPurchasingClient`, implementert som partial i `FikenClient`, dekker innboks, EHF, kjøp, salg, kjøpsutkast, vedlegg, kontoer og leverandører. Nedlasting av bilag sender bare bearer-token til https-adresser hos fiken.no.
- **Sikringer:**
  - Bare kjøpsutkast (`cash=false`, `paid=false`). Bokføring og betaling finnes ikke i koden.
  - To steg: `preview_purchase_draft` gir et engangstoken (15 min), og `create_purchase_draft` tar bare tokenet.
  - Skriving er bare tillatt i testforetak eller i slugs listet i `Mcp__AllowDraftsForCompany`.
  - Bekreftelse via MCP-elicitation (dialog fra serveren, ikke via modellen) før opprettelse og vedlegg. I ekte foretak kreves elicitation, med mindre `Mcp__AllowDraftsWithoutElicitation=true`. Bilagets status sjekkes på nytt før opprettelse.
  - Validering: konto i kontoplanen (balanse- og inntektskonto avvises), prosjekt finnes, øre-avrunding, sum mot EHF, leverandør mot EHF, duplikat på fakturanummer, kreditnota, valuta og MVA-type.
- **Web og Mcp samtidig:**
  - Låsfiler i datamappen: `fiken-oauth.lock` hindrer at to prosesser fornyer token samtidig, og `fiken-api.lock` gir 1 kall om gangen og 260 ms mellomrom.
  - `config.lock` beskytter lese-endre-skrive av `config.json`.
  - Endringen ligger også i egen branch `kryssprosess-las` (PR mot `main`).
- **Analyse:**
  - `ProjectVarianceExplainer` og `AnomalyFinder` bruker Fase 3-hjelperne `BudgetReportBuilder.TryGetResultAmount` og `PrimaryProjectId`, så fortegnet finnes ett sted.
  - Fortegnsregelen er verifisert mot ekte Fiken-data i Fase 3-tråden (2025 stemmer på øret).
- **Gjenstår:**
  - Test hele flyten mot testforetak på Windows-maskinen. Kontroller MVA-type (`NONE`, netto = brutto) på kjøpsutkast, og at vedlegg via `?inboxDocumentId=` fungerer uten filinnhold.
  - EHF-stien er bare testet med falske data, fordi testforetak trolig ikke kan motta EHF.
  - Kreditnotaer og opprettelse av nye leverandører støttes ikke (bevisst).
  - HTTP-transport kommer i Fase 5.

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

## Fase 6 – Idébank for videre utvidelser *(Prioritert; kvalitetssjekk v1 under arbeid i branch `fase6-ideer`)*
**Mål:** Små verktøy som sparer kassereren tid før styremøter, årsoppgjør og tilskuddssøknader, uten å gjøre det samme som Fase 2–5.

**Funn i Fiken API v2 som styrer prioriteringen:**
- API-et har ingen kontoutskrift eller banktransaksjoner, bare saldo per bankkonto (`GET /bankBalances`). Bankavstemming må bygge på eksportfiler.
- Betaling på salg (`POST /sales/{id}/payments`) føres direkte og er ikke et utkast. Avstemming kan derfor bare lage en rapport med forslag.
- Åpne fakturaer, salg og kjøp (`invoices?settled=false`, `sales?settled=false`, `purchases?paid=false`) og ubrukte bilag i innboksen (`inbox?status=unused`) kan hentes bare med lesekall.

**Prioritert rekkefølge:**
1. **Kvalitetssjekk før styremøte og årsoppgjør** *(først, størrelse S)*
   - v1 (valgt av kasserer): ubetalte kundefakturaer, åpne salg (annet salg) og ubetalte kjøp, med forfalt/åpen per valgt dato, summer og kopierbar tabell for Word/Google Docs.
   - Logikken ligger i `FikenHjelper.Core/QualityCheck` (`QualityCheckService`, `QualityCheckRules`, `QualityCheckTextFormatter`) og bruker bare GET-kall. Salg og kjøp hentes med Fase 4s `IFikenPurchasingClient` og samme modeller (`FikenSale`, `FikenPurchase`).
   - Overlapp med Fase 4: `AnomalyFinder.OverduePurchases`/`OverdueSales` i `find_accounting_anomalies` gjør en enklere variant. Senere kan de bruke `QualityCheckRules`, så kreditsaldoer, valuta og fakturasalg håndteres likt.
   - Neste steg: siden `/kvalitetssjekk` med MudBlazor (Fase 3 og MudBlazor er nå i `main`).
   - Senere: ubrukte bilag i innboksen, fakturautkast som aldri ble sendt, bank i Fiken mot banksaldo, og Fase 3s «bilag uten prosjekt» og «prosjekter uten budsjettpost» (gjenbrukes, ikke skrevet på nytt).
   - Kreditsaldoer (overbetaling, kreditnota som ikke er motregnet) vises med egen status og trekkes fra summen. Poster der beløpet ikke kan regnes ut sikkert (fremmed valuta, fakturasalg uten treff), merkes «Sjekk» og holdes utenfor summen.
   - Tabulatorseparert tekst bruker tall uten tusenskille (`1234,50`), så regneark leser dem som tall. Markdown bruker `1 234,50`.
   - *Begrensning:* listen viser åpne poster og utestående beløp slik de er i Fiken i dag. En post som ble betalt etter valgt dato, kommer ikke med, og delbetalinger etter valgt dato er trukket fra beløpet. Dette må stå tydelig på siden.
2. **Tilskuddsgrunnlag** *(størrelse S–M per ordning)*
   - Aktuelt: fylkeskommunalt tilskudd og kommunale tilskudd eller stiftelser. Frifond og momskompensasjon er ikke aktuelle nå.
   - Bygger på Fase 3s summer per konto og prosjekt og xlsx-eksporten derfra.
3. **Utlegg og reiseregninger fra frivillige** *(størrelse M–L, mest i Fase 4)*
   - Innboks, kontoforslag og kjøpsutkast med vedlegg finnes allerede i MCP-serveren (Fase 4). Fase 6 lager eventuelt bare et enkelt skjema senere, for frivillige som ikke bruker AI-verktøy.
4. **Betalings- og bankavstemming** *(størrelse M, sist)*
   - Kretsen har ikke egen Vipps. Zettle ble brukt på leir sommeren 2026.
   - Første steg uten fil: ubetalte fakturaer per arrangement (orderReference/prosjekt fra Fase 1), som delvis dekkes av kvalitetssjekken.
   - Import av Zettle-eksport når neste leir nærmer seg og filimporten fra Fase 2 finnes.

**Åpne spørsmål:**
- Hvordan føres Zettle-salg i Fiken i dag? Er det ett samlebilag per utbetaling?
- Hvilke kommuner og stiftelser søker kretsen hos, når er fristene, og hvilke tall krever hver søknad eller rapport?
- Hvordan kommer utlegg fra frivillige inn i dag (e-post til Fiken-innboksen, skjema eller papir)?
