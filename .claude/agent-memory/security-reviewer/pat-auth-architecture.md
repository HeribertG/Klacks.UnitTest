---
name: pat-auth-architecture
description: PAT-System Security-Architektur — vollständig reviewed 2026-06-12, Findings und Entscheidungen dokumentiert
metadata:
  type: project
---

## PAT-System — Review 2026-06-12

### Token-Generierung
- CSPRNG: `RandomNumberGenerator.GetBytes(32)` = 256 Bit Entropie. Sicher.
- Encoding: Base64Url = 43 Zeichen. Plaintext = "klacks_pat_" + 43 Zeichen = 54 Zeichen gesamt.
- Hash: SHA-256 ohne Salt. Akzeptiert: Token ist hochentropisch (256 Bit), Salt bei SHA-256 bringt keinen Sicherheitsgewinn.

### Hash-Speicherung
- Nur `TokenHash` (SHA-256 hex, 64 Zeichen) in DB — kein Klartext persistiert.
- `TokenPrefix` (erste 12 Zeichen des Plaintext) für UI-Anzeige — kein Security-Problem, nur Identifikationshilfe.

### Timing-sicherheit
- `GetByHashAsync` sucht per `t.TokenHash == tokenHash` — EF Core LINQ → parameterisiertes SQL → konstante DB-Zeit bei Hit/Miss. Kein klassischer Timing-Angriff auf String-Vergleich, aber DB-Lookup-Zeit ist minimal timing-unterschiedlich bei Hit vs. Miss. Praxisrelevanz: vernachlässigbar bei DB-Roundtrip-Overhead.

### Offenes Finding: IsAuthorised-Claim fehlt
`BuildPrincipalAsync` in `PatAuthenticationHandler.cs` Zeile 112ff baut Claims aus User + Roles, aber setzt den `IsAuthorised`-Claim NICHT, der für Supervisor-Funktionen gebraucht wird. JWT-Login setzt diesen Claim. Supervisor-Endpoints prüfen `IsAuthorised = true`. Ein PAT eines Supervisors hätte also keine Supervisor-Rechte. Dies kann Absicht sein (PATs sind bewusst beschränkt) oder ein Bug — zu klären mit dem User.

### Token-Verwaltungs-API
- `PersonalAccessTokensController` hat `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]` — explizit auf JWT gepinnt, PAT kann sich nicht selbst verwalten. Korrekt.
- IDOR-Schutz: `RevokeAsync(id, userId)` und `GetByUserAsync(userId)` sind owner-scoped. Korrekt.
- Klartext nur in `PersonalAccessTokenCreatedDto.Token` — einmalig in Create-Response. Korrekt.

### Logging
- `_logger.LogInformation("Personal access token {TokenId} created", created.Id)` — nur ID geloggt, kein Token-Material. Korrekt.

### Expiry
- UTC durchgängig: `DateTime.UtcNow`, DB-Spalten `timestamp with time zone`. Korrekt.
- `ExpiresAt.Value <= utcNow` — korrekte Grenze (abgelaufene Token bei exakter Sekunde werden abgelehnt).

**Why:** Vollständiger Security-Review der PAT-Implementierung
**How to apply:** Bei zukünftigen PAT-Änderungen IsAuthorised-Finding beachten
