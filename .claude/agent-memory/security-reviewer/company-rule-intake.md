---
name: company-rule-intake
description: Security review of the Klacksy Company-Rule-Intake feature (7 skills + handlers) — permission chain, injection surfaces, draft store isolation, HITL bypass paths
metadata:
  type: project
---

Reviewed 2026-07-16. Feature: Klacksy chat collects company rules (surcharge settings /
counter rules / custom macros) via 7 skills in `Application/Skills/CompanyRules/` +
`Application/Handlers/CompanyRules/`. No new REST controllers (confirmed via grep — only
`ICompanyRuleRepository`/`CompanyRuleRepository` exist, no controller).

## Confirmed safe
- **Permission gate**: `SkillExecutorService.ValidatePermissions` (Domain/Services/Assistant/Skills/SkillExecutorService.cs:229) requires literal `Roles.Admin` or `CanEditSettings` in `context.UserPermissions`. `Permissions.GetPermissionsForRole` (Domain/Constants/Permissions.cs) grants `CanEditSettings` ONLY to `Roles.Admin` — `Authorised` (Supervisor) does not get it. Both entry paths (ChatController, SkillsController) converge on Admin-only for these skills.
- **HITL for apply/revert**: both are in `SkillRiskClassifier.SensitiveSkills` (Application/Skills/Meta/SkillRiskClassifier.cs) → `AutonomyGateService.IsAllowed` returns `false` unconditionally for Sensitive → always needs a server-issued one-time confirmation token, same-turn redemption refused (`ITurnConfirmationScope`).
- **Bypass paths checked, all closed for Sensitive skills**:
  - `ScheduleRecurringTaskSkill.cs:123-126` explicitly refuses to schedule any skill classified `Sensitive` ("too sensitive to run unattended").
  - `PlanStepExecutor.RequiresApproval` (Application/Services/Assistant/Planning/PlanStepExecutor.cs:171) always pauses on Sensitive skills regardless of autonomy level/reversible flag; resume is gated by `AgentPlansController.ApproveAndContinue` which checks `plan.UserId == callerUserId`.
  - `ConfirmPendingActionSkill` bypass is legitimate — it only fires after a real one-time token was consumed.
- **No arbitrary setting-key write**: `CompanyRuleSurchargeSettingMap.ParameterToSettingKey` (Domain/Constants/) is a fixed, hardcoded dictionary; apply/revert only ever touch these keys.
- **Macro path validated**: `ApplyCompanyRuleCommandHandler.ApplyCustomMacroAsync` routes through the real `MacroCommands.PostCommand` → `Application/Handlers/Settings/Macro/PostCommandHandler.cs:43 EnsureValidScript` → `IMacroScriptValidator.Validate` (compile + 5s-timeout probe-execute, per known parser-hang fix). No bypass of validation for the company-rule path.
- **Value validation**: `CompanyRuleDraftValidator.Validate` (Domain/Services/Settings/) rejects unknown parameter names, enforces Time/Integer/Decimal/Enum per catalog definition with range checks. Text-typed fields (MacroScript/MacroName/Description/SchedulingRuleName) are unrestricted free text by design — acceptable since script content is validated later at apply, and the rest are inert display/lookup strings.
- **Draft store isolation**: `InMemoryPendingCompanyRuleDraftStore` keys by `{userId:N}|conversationKey` (Infrastructure/Services/Assistant/) — real JWT-derived `userId`, not attacker-controlled. `CompanyRuleDraftScope.ConversationKey` uses a fixed constant when SessionId is unset (normal chat flow) — one draft per admin, no cross-user leak. Not a meaningful DoS vector (Admin-only feature, one draft per user overwrites the previous).
- **No insecure deserialization**: `SettingsSnapshotJson`/`AppliedParametersJson` are `Dictionary<string,string?>` via `System.Text.Json`, no polymorphic type resolution, and are never exposed via `CompanyRuleResourceFactory.ToResource` (not leaked back to the LLM/chat).
- **No tenant-IDOR**: Klacks is not actually multi-tenant at the entity level (only `IdentityProvider`/`SkillUsageRecord` carry `TenantId`); `CompanyRule` has none, consistent with the rest of Settings/Macro/CounterRule.

## Pre-existing framework quirk (not a CompanyRule-specific bug)
`SkillsController.GetCurrentUserPermissions()` (Presentation/Controllers/Assistant/SkillsController.cs:118) passes raw JWT role-claim values straight through as "permissions" — unlike `ChatController.GetCurrentUserRights()` (line 474) which correctly expands roles via `Permissions.GetPermissionsForRole`. Effect for CompanyRule skills: fails closed (denies legitimate non-Admin CanEditSettings holders via the direct REST `/api/backend/skills/execute` endpoint) — not a privilege-escalation risk since the Admin-bypass check still short-circuits correctly. Worth flagging as "zu diskutieren" if seen again elsewhere, not as a CompanyRule vulnerability.

## Minor/accepted
- `MacroScriptValidator` leaks one thread-pool worker per hung-parser probe (documented, accepted design per prior macro-hardening session). Company-rule custom-macro apply inherits this; only Admin-triggered self-DoS, low severity.
- `RestoreSurchargeSnapshotAsync` (Application/Handlers/CompanyRules/RevertCompanyRuleCommandHandler.cs:82) does not re-validate snapshot keys against `CompanyRuleSurchargeSettingMap` before writing — safe today because the only writer of `SettingsSnapshotJson` is the apply handler itself (no controller exposes it), but a defense-in-depth key-allowlist check would remove the implicit trust-the-writer assumption.
