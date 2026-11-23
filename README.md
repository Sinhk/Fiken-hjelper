# Fiken-hjelper

Lokale verktøy i **.NET 10** for regnskapsfører/kasserer i **Romsdal og Nordmøre krets (Norges speiderforbund)**.

Se [PLAN.md](PLAN.md) for hele veikartet (CSV/XLSX-import, budsjettrapporter i Excel, MCP-server for AI-chat og .NET Aspire-støtte).

## Kom i gang

### 1. Forutsetninger
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Fiken-konto med API-tilgang aktivert (*Foretak → Tilleggstjenester*)
- Egen OAuth2-utviklerapp i Fiken (anbefalt, krever ikke godkjenning på forhånd) – eller personlig API-nøkkel fra `api@fiken.no`
- Prosjekt-ID og API-nøkkel fra `min.speiding.no` (*Prosjekter → Webtjenester*)

> **Tips:** Du kan opprette et gratis testforetak i Fiken for å prøve hele flyten uten å berøre kretsens ekte regnskap.

### 2. Sett opp Fiken OAuth2 (anbefalt)
1. Logg inn i Fiken, gå til **Rediger konto → Profil → Andre innstillinger** og kryss av for at du er **utvikler**.
2. Gå til **API**-fanen under brukerinnstillinger og opprett en ny app (f.eks. *Fiken-hjelper RNK*).
3. Legg inn begge de lokale Redirect URI-ene i appen (avhengig av om du starter med `http` eller `https`):
   - `https://localhost:7193/fiken/oauth/callback`
   - `http://localhost:5066/fiken/oauth/callback`
4. Under *Avanserte innstillinger* kan du velge **Uten client secret (PKCE)** (anbefalt for lokale apper), eller beholde både `Client ID` og `Client Secret`.
5. Start appen (se steg 3), åpne **Innstillinger**, lim inn `Client ID` (og evt. `Client Secret`) og klikk **«Lagre og logg inn med Fiken»**.

Alternativt kan du lagre `ClientId`/`ClientSecret` eller en personlig API-nøkkel med `dotnet user-secrets`:

```powershell
dotnet user-secrets --project src/FikenHjelper.Web set "Fiken:ClientId" "DIN_CLIENT_ID"
# Valgfritt hvis du bruker confidential client:
dotnet user-secrets --project src/FikenHjelper.Web set "Fiken:ClientSecret" "DIN_CLIENT_SECRET"
# Eller hvis du har personlig API-nøkkel:
dotnet user-secrets --project src/FikenHjelper.Web set "Fiken:ApiToken" "DIN_PERSONLIGE_FIKEN_NOKKEL"
```

Tokens og innstillinger lagres lokalt utenfor Git i `%LocalAppData%\FikenHjelper\config.json` og fornyes automatisk.

### 3. Start web-appen

```powershell
dotnet run --project src/FikenHjelper.Web
```

Åpne lenken som vises i terminalen (f.eks. `http://localhost:5066` eller `https://localhost:7193`), og gå til **Innstillinger** eller **Arrangementsfakturering**.

### 4. Kjør tester

```powershell
dotnet test
```

### 5. Start med .NET Aspire (valgfritt)

Aspire gir et lokalt **Aspire Dashboard** med logger, traces og metrikker – blant annet alle HTTP-kall mot `api.fiken.no` og `min.speiding.no`. Det krever ingen workload eller templates; alt hentes som NuGet-pakker ved første bygg (første gang tar det litt tid).

```powershell
# Windows (HTTPS-dashboard, krever klarert dev-sertifikat: dotnet dev-certs https --trust)
dotnet run --project src/FikenHjelper.AppHost

# Linux (HTTP-dashboard, slipper klarert dev-sertifikat)
dotnet run --project src/FikenHjelper.AppHost --launch-profile http
```

Dashboard-lenken (med innloggingstoken) vises i terminalen. Web-appen kjører fortsatt på `https://localhost:7193` / `http://localhost:5066`, så OAuth-redirect fungerer som før. Uten Aspire startes appen som vanlig (steg 3) og sender da ingen telemetri.

Hemmeligheter kan valgfritt legges som Aspire-parametere i AppHost sine user-secrets (de sendes til web-appen som miljøvariabler og maskeres i dashboardet). Web-appens egne user-secrets og `config.json` fungerer uansett:

```powershell
dotnet user-secrets --project src/FikenHjelper.AppHost set "Parameters:fiken-client-id" "DIN_CLIENT_ID"
dotnet user-secrets --project src/FikenHjelper.AppHost set "Parameters:fiken-client-secret" "DIN_CLIENT_SECRET"
dotnet user-secrets --project src/FikenHjelper.AppHost set "Parameters:fiken-api-token" "DIN_PERSONLIGE_FIKEN_NOKKEL"
```

> `FikenHjelper.ServiceDefaults` legger **ikke** på standard resilience/retries på HttpClients – Fiken-klienten bruker kun sin egne `FikenRateLimitHandler`. Query-strenger (f.eks. OAuth `code`/`state`) redigeres alltid i traces.

### 6. CI

GitHub Actions (`.github/workflows/ci.yml`) kjører på alle pull requests mot `main` og ved push til `main`. Sjekkene er:
- bygg i Release der kompilatoradvarsler gir feil (`-warnaserror`)
- formateringssjekk med `dotnet format --verify-no-changes` (kjør `dotnet format` lokalt for å rette opp)
- alle tester, med resultater i jobbsammendraget og som nedlastbar `test-results`-artefakt

`main` krever at CI er grønn før en PR kan merges.

## Sikkerhetsprinsipper
- Alle fakturaer opprettes **kun som utkast** i Fiken (`POST /companies/{slug}/invoices/drafts`). Ingenting bokføres eller sendes automatisk.
- Kretsen er ikke MVA-pliktig, så alle linjer sendes med `vatType = "NONE"`.
- Fiken API tillater maks 1 samtidig kall og maks 4 kall per sekund – dette håndheves automatisk i `FikenRateLimitHandler`.
