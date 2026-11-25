# Fiken-hjelper – instruksjoner for kodeagenter

Gjelder alle agenter (Claude Code, Antigravity m.fl.). `CLAUDE.md` importerer denne filen.

## Regler for Fiken
- Appen lager bare **utkast** i Fiken. Den skal aldri sende, fullføre eller bokføre fakturaer.
- Mot kretsens ekte regnskap (det foretaket som brukes til vanlig) er bare **lesing (GET)** lov ved testing.
- Alt som oppretter, endrer eller sletter data (POST/PUT/PATCH/DELETE), testes bare mot et eget testforetak i Fiken.

## Persondata og hemmeligheter
- Ingen persondata eller hemmeligheter i Git: ikke eksportfiler, ikke tokens, ikke lokale innstillinger.
- Testdata skal være syntetiske: oppdiktede navn, `@example.no`, medlemsnumre som 900001.
- Eksempelfiler fra brukeren (f.eks. eksporter fra Min speiding) kan inneholde ekte persondata. De leses bare lokalt og kopieres aldri inn i repoet, tester eller commit-meldinger.

## Arbeidsflyt
- Brukeren jobber fra flere maskiner. Windows-maskinen Kaladin har Fiken-innloggingen, så alt som trenger ekte Fiken-data, må verifiseres der.
- Jobb på en egen branch. Hent `origin/main` jevnlig og rebase før større steg, siden andre maskiner og agenter også committer.
- Commit gjerne lokalt, men **spør før du pusher**.
- Bruk av porter når du kjører appen lokalt:
  - 5066 og 7193 er ofte opptatt av en `dotnet watch` som kjører fra før. Sjekk først, og bruk en annen port (`--urls http://localhost:51xx`).
  - Stopp bare prosesser du selv har startet, og gjør det med eksakt PID, aldri `pkill -f` på mønster.

## Valg av løsninger
- Bruk modne, aktivt vedlikeholdte open source-pakker med åpen lisens framfor å skrive egne parsere eller infrastruktur. Eksempler: CsvHelper for CSV, ClosedXML for Excel, MudBlazor for UI-komponenter. Skriv bare den tynne limkoden rundt.
- Brukergrensesnitt, feilmeldinger og dokumentasjon skrives på norsk bokmål.
- Kjør `dotnet build`, `dotnet test` og `dotnet format --verify-no-changes` før commit. CI (`.github/workflows/ci.yml`) kjører de samme sjekkene på alle PR-er mot `main`, og bygget feiler på kompilatoradvarsler.

## Eksportfiler fra Min speiding
Disse konvensjonene går ikke fram av kolonnenavnene:
- Tomme felt eksporteres ofte som `-`, f.eks. `Avmeldingsdato` = `-` for deltakere som ikke er avmeldt.
- Filene er kommaseparerte, alle felt står i anførselstegn, og kodingen er UTF-8 uten BOM.
- Individuell eksport (`<Arrangement>, <Krets>, Deltaker - deltakere.csv`):
  - Det finnes to kolonner som begge heter `E-post`.
  - `Deltakeravgift` står som `<Navn>: <beløp>`, f.eks. `Lederkurs: 700.00`.
  - `Totalbeløp` er et rent tall.
  - Svar på påmeldingsskjemaet kommer som kolonner med navn `<Skjema> - <Spørsmål>`, f.eks. `Generelt - Mobilnummer`. De varierer per arrangement og trengs ikke i appen.
- Gruppe-/fullrapport (`projectParticipantsReportFull_*.csv`):
  - `Antall` inneholder et beløp (`0,00`), ikke et antall plasser.
  - `Bekreftet` inneholder tidspunktet for bekreftelsen (`2026-11-15 06:15`), ikke Ja/Nei.
  - `Til stede` blir `Nei` når deltakeren sjekkes ut. Etter arrangementet har derfor nesten alle `Nei` der. Bruk `Innsjekket` for å avgjøre om noen faktisk møtte opp.
- Begge eksportene inneholder sensitive kolonner (fødselsdato, adresse, telefon, foresattes kontaktinfo, politiattest). Ikke ta dem med videre i appen uten at det trengs.
