# Security Reviewer — Agent Memory

- [PAT Auth Architecture](pat-auth-architecture.md) — PAT-System vollständig reviewed 2026-06-12: SHA-256 ohne Salt (akzeptiert, da HE-Token), timing-unsafe Vergleich via Hash-Lookup (kein Finding wegen gleichwertiger Sicherheit durch SHA-256), IsAuthorised-Claim fehlt in BuildPrincipalAsync (offenes Finding), Token-Verwaltungs-API korrekt auf JWT-only gepinnt
- [MCP Resources Security](mcp-resources-security.md) — McpResourceCatalog reviewed 2026-06-12: DocsReader ist compile-time Allowlist (embedded resources), keine Path-Traversal möglich; MCP-Endpoint erfordert explizite Auth-Schemes (JWT+PAT), kein Default-Scheme-Bug
- [Known Issues](known-issues.md) — Bekannte pre-existing Build-Errors und akzeptierte Abweichungen
