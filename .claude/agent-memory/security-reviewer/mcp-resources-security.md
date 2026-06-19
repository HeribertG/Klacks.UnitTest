---
name: mcp-resources-security
description: MCP-Endpoint und McpResourceCatalog Security-Review 2026-06-12
metadata:
  type: project
---

## MCP Resources — Review 2026-06-12

### Path-Traversal-Schutz
`McpResourceCatalog.ReadResourceAsync` extrahiert `docName` aus URI und prüft via `DocsReader.DocExists(docName)`.
`DocsReader` ist eine compile-time Allowlist (Dictionary mit 7 festen Keys) + `GetManifestResourceStream` auf embedded resources.
`GetManifestResourceStream` kann keine Filesystem-Pfade traversieren — nur embedded Ressourcen im Assembly.
Ergebnis: Path-Traversal ist strukturell unmöglich. Kein Finding.

### MCP Auth-Konfiguration
`MapKlacksMcp()` pinnt explizit `.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, PatConstants.SchemeName)`.
Gleiche Falle wie SignalR-Hubs (AddIdentity überschreibt Default-Scheme) — hier korrekt gelöst.

### DocsReader Allowlist
Keys: general, clients, shifts, identity-providers, macros, calendar-rules, ai-system.
Neue Docs müssen bewusst in AvailableDocs eingetragen werden — keine automatische Exposition.

**Why:** Vollständiger Security-Review des MCP-Resource-Systems
**How to apply:** Bei neuen MCP-Resource-Typen prüfen ob Allowlist-Prinzip erhalten bleibt
