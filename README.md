# Fiken-hjelper

Lokale verktøy i **.NET 11** for regnskapsfører/kasserer i **Romsdal og Nordmøre krets (Norges speiderforbund)**.

Se [PLAN.md](PLAN.md) for hele veikartet (CSV/XLSX-import, budsjettrapporter i Excel, MCP-server for AI-chat og .NET Aspire-støtte).

## Kom i gang

### 1. Forutsetninger
- [.NET 11 SDK](https://dotnet.microsoft.com/download)
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

### 4. MCP-server for AI-chat (Claude Desktop, VS Code, Antigravity)

`FikenHjelper.Mcp` gir AI-klienter verktøy for innkommende fakturaer (innboks/EHF → forslag til konto og prosjekt → **kjøpsutkast**), budsjettstatus, avviksforklaring og kontroll av regnskapet. Serveren bruker samme innlogging og datamappe som web-appen, så logg inn via web-appen først.

**Sikkerhet:**
- Bare kjøpsutkast kan opprettes. Ingenting bokføres eller betales.
- Hvert utkast forhåndsvises med `preview_purchase_draft` og opprettes bare med tokenet derfra, etter at du har sagt ja.
- I et foretak som ikke er testforetak er serveren **kun lesende**, til du selv legger slugen inn i `Mcp__AllowDraftsForCompany`.
- Støtter klienten elicitation, viser serveren en egen bekreftelsesdialog før utkastet opprettes. Da kan ikke tekst i et bilag lure AI-en til å opprette noe. I ekte foretak kreves denne dialogen, med mindre du setter `Mcp__AllowDraftsWithoutElicitation=true`.
- Kjøpsutkast får MVA-type `NONE` (netto = brutto), siden kretsen ikke er MVA-registrert.

Publiser serveren én gang (og etter oppdateringer). `dotnet run` kan skrive byggmeldinger til stdout og forstyrre protokollen.

```powershell
dotnet publish src/FikenHjelper.Mcp -c Release -o "$env:LOCALAPPDATA\FikenHjelper\mcp"
```

**Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "fiken-hjelper": {
      "command": "C:\\Users\\<bruker>\\AppData\\Local\\FikenHjelper\\mcp\\FikenHjelper.Mcp.exe",
      "env": {
        "Mcp__CompanySlug": "<slug-til-testforetak>"
      }
    }
  }
}
```

**VS Code** (`.vscode/mcp.json` eller brukerinnstillinger) og **Antigravity** (`mcp_config.json`) bruker samme oppsett: `"type": "stdio"` (VS Code), `command` og `env` som over.

Valgfrie miljøvariabler (`env`):

| Variabel | Betydning |
|---|---|
| `Mcp__CompanySlug` | Bruk et annet foretak enn web-appen, f.eks. testforetaket. Lagres ikke. |
| `Mcp__AllowDraftsForCompany` | Kommaseparerte slugs der kjøpsutkast er tillatt selv om det ikke er testforetak (f.eks. kretsens ekte foretak, når du er klar). |
| `Mcp__AllowDraftsWithoutElicitation` | `true` tillater kjøpsutkast i ekte foretak selv om klienten ikke kan vise serverens bekreftelsesdialog (elicitation). Da er klientens godkjenning av verktøykallet eneste bekreftelse, så ikke velg «Always allow» for `create_purchase_draft`. |
| `FikenHjelper__DataDirectory` | Annen datamappe enn `%LocalAppData%\FikenHjelper`. |

Web-appen og MCP-serveren kan kjøre samtidig. Fornyelse av Fiken-innloggingen og grensen på 1 kall om gangen koordineres med låsfiler i datamappen. Du kan teste serveren med MCP Inspector: `npx @modelcontextprotocol/inspector <sti til FikenHjelper.Mcp.exe>`.

### 5. Kjør tester

```powershell
dotnet test
```

### Git-hooks
Første `dotnet build` installerer git-hooks med [Husky.Net](https://alirezanet.github.io/Husky.Net/) (`.husky/`):
- **pre-commit:** [gitleaks](https://github.com/gitleaks/gitleaks) stopper commits med hemmeligheter, og `dotnet format` sjekker endrede `.cs`-filer.
- **pre-push:** gitleaks sjekker alle commits som ikke er pushet.

Bygg og tester kjøres ikke i hookene, siden CI krever grønt bygg før merge.

gitleaks må installeres separat: `winget install --id Gitleaks.Gitleaks -e` (Windows) eller `brew install gitleaks` (macOS/Linux). Hookene installeres ikke i CI eller i en kopi uten `.git` (f.eks. nedlastet ZIP), og de slås av med miljøvariabelen `HUSKY=0`.

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

> `FikenHjelper.ServiceDefaults` legger **ikke** på standard resilience/retries på HttpClients – Fiken-klienten bruker kun sin egen `FikenRateLimitHandler`. Query-strenger (f.eks. OAuth `code`/`state`) redigeres alltid i traces.

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

## Lisens
[MIT](LICENSE).
