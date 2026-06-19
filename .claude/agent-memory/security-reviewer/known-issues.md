---
name: known-issues
description: Bekannte pre-existing Build-Errors, akzeptierte Security-Abweichungen und offene Findings im Klacks-Projekt
metadata:
  type: project
---

## Pre-existing Build-Errors (ignorieren)
- `ScheduleReportGenerator.cs` — Build-Error bekannt, ignorieren
- `WorkRepository.cs:341` — Build-Error bekannt, ignorieren
- SCSS-Errors in `report-row` / `report-header` — ignorieren

## Akzeptierte Abweichungen
- SHA-256 ohne Salt für PAT-Hashes: Token ist 256-Bit-CSPRNG, Salt nicht notwendig. Bewusste Entscheidung.
- Token in localStorage (falls Frontend es dort speichert): bekanntes XSS-Risiko, in Klacks-Kontext (interne App) als mittel eingestuft.

## Offene Findings (zu klären)
- `PatAuthenticationHandler.BuildPrincipalAsync` setzt keinen `IsAuthorised`-Claim → PAT-User haben keine Supervisor-Rechte. Ob Absicht oder Bug: mit User klären.
  Datei: `/mnt/c/SourceCode/Klacks.Api/Infrastructure/Authentication/PatAuthenticationHandler.cs` Zeile 110-132
