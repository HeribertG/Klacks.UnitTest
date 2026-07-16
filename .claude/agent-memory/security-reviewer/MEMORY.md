# Security Reviewer — Agent Memory

- [PAT Auth Architecture](pat-auth-architecture.md) — PAT-System vollständig reviewed 2026-06-12: SHA-256 ohne Salt (akzeptiert, da HE-Token), timing-unsafe Vergleich via Hash-Lookup (kein Finding wegen gleichwertiger Sicherheit durch SHA-256), IsAuthorised-Claim fehlt in BuildPrincipalAsync (offenes Finding), Token-Verwaltungs-API korrekt auf JWT-only gepinnt
- [MCP Resources Security](mcp-resources-security.md) — McpResourceCatalog reviewed 2026-06-12: DocsReader ist compile-time Allowlist (embedded resources), keine Path-Traversal möglich; MCP-Endpoint erfordert explizite Auth-Schemes (JWT+PAT), kein Default-Scheme-Bug
- [Known Issues](known-issues.md) — Bekannte pre-existing Build-Errors und akzeptierte Abweichungen
- [Company-Rule-Intake Security Review](company-rule-intake.md) — 2026-07-16: alle 5 geprüften Fragen sauber (Admin-only Permission-Kette, Sensitive-HITL ohne Bypass-Lücke, fixe Setting-Key-Map, Macro-Pfad validiert, Draft-Store userId-isoliert); SkillsController-Permission-Übersetzung fehlt (fail-closed, kein Escalation-Risiko, pre-existing Framework-Quirk)
