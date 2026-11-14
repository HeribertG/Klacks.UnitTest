// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shrink-only baseline of SilentCatchAllGuardTests: "path relative to Klacks.Api -> number of silent catch-all
/// blocks the file had when it was listed". An entry is a ceiling AND a floor - a file above its number fails, and so
/// does a file below it, so every cleanup lowers (or removes) its entry in the same change. Never add an entry and
/// never raise a number: catch the specific type the callee throws, or log, or rethrow.
///
/// Start (2026-11-14): 91 silent catch-all blocks in 71 files. Part of them are catches that hand the exception to a
/// helper that logs elsewhere or turns it into an error result (ErrorHandlingMiddleware, SkillExecutorService, job
/// runners); the guard cannot see that and counts them, so they stay listed until they log or rethrow in the catch.
/// The count is per file, so a swap (one block fixed and a new one added in the same file) is not detected, exactly as
/// in MagicNumberGuardAllowlist.
/// </summary>

namespace Klacks.UnitTest.Architecture;

public static class SilentCatchAllGuardAllowlist
{
    public static readonly IReadOnlyDictionary<string, int> Entries = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Application/Handlers/Assistant/InstallWhisperPluginCommandHandler.cs"] = 1,
        ["Application/Handlers/Assistant/UninstallWhisperPluginCommandHandler.cs"] = 1,
        ["Application/Handlers/Orders/SealOpenOrdersCommandHandler.cs"] = 1,
        ["Application/Handlers/Update/RequestRollbackCommandHandler.cs"] = 1,
        ["Application/Handlers/Update/TriggerUpdateCommandHandler.cs"] = 1,
        ["Application/Klacksy/AssistantTextsPluginLoader.cs"] = 2,
        ["Application/Klacksy/CompletionClaimPluginLoader.cs"] = 1,
        ["Application/Klacksy/ConversationSignalsPluginLoader.cs"] = 2,
        ["Application/Klacksy/GroupingIntentPluginLoader.cs"] = 1,
        ["Application/Klacksy/MutationIntentPluginLoader.cs"] = 1,
        ["Application/Klacksy/NavigationIntentPluginLoader.cs"] = 1,
        ["Application/Mappers/ScheduleMapper.Schedules.cs"] = 1,
        ["Application/Services/Assistant/GreetingComposer.cs"] = 1,
        ["Application/Services/Assistant/Learning/SkillExecutionOracle.cs"] = 1,
        ["Application/Services/Assistant/Triggers/PeriodAutoCloseService.cs"] = 1,
        ["Application/Services/Exports/ExportFormatOverridePatch.cs"] = 1,
        ["Application/Services/Imports/ErpImportStoredFileVerifier.cs"] = 1,
        ["Application/Services/Playground/PlaygroundDeviceTokenService.cs"] = 1,
        ["Application/Services/Schedules/ContainerTemplateLockScope.cs"] = 1,
        ["Application/Services/Schedules/ScheduleCompletionService.cs"] = 1,
        ["Application/Skills/AssignContainerWeekSkill.cs"] = 1,
        ["Application/Skills/BulkDeleteWorksSkill.cs"] = 1,
        ["Application/Skills/Generic/GenericDeleteExecutor.cs"] = 1,
        ["Application/Skills/Generic/GenericListExecutor.cs"] = 1,
        ["Application/Skills/ImportCalendarRulesSkill.cs"] = 1,
        ["Application/Skills/PlaceWorkSkill.cs"] = 1,
        ["Application/Skills/ReassignWorkSkill.cs"] = 1,
        ["Application/Skills/RestoreWorkSkill.cs"] = 1,
        ["Application/Skills/SealOpenOrdersSkill.cs"] = 1,
        ["Application/Skills/SetClientQualificationSkill.cs"] = 1,
        ["Application/Skills/SetShiftRequiredQualificationSkill.cs"] = 1,
        ["Application/Skills/SplitShiftsByWeekdaySkill.cs"] = 1,
        ["Application/Skills/ValidateAddressSkill.cs"] = 1,
        ["Domain/Services/Assistant/ProviderStreamReader.cs"] = 1,
        ["Domain/Services/Assistant/Skills/Adapters/BaseSkillAdapter.cs"] = 1,
        ["Domain/Services/Assistant/Skills/Implementations/ValidateCalendarRuleSkill.cs"] = 1,
        ["Domain/Services/Assistant/Skills/RecentEntityExtractor.cs"] = 1,
        ["Domain/Services/Assistant/Skills/SkillExecutorService.cs"] = 2,
        ["Domain/Services/Assistant/Skills/SkillParameterReader.cs"] = 1,
        ["Infrastructure/Email/EmailService.cs"] = 1,
        ["Infrastructure/Email/EmailTestService.cs"] = 1,
        ["Infrastructure/Email/EmailWrapper.cs"] = 2,
        ["Infrastructure/Email/ImapTestService.cs"] = 1,
        ["Infrastructure/Exceptions/ErrorHandlingMiddleware.cs"] = 1,
        ["Infrastructure/Hosting/FailSafeLogger.cs"] = 1,
        ["Infrastructure/Hosting/FailSafeLoggerProvider.cs"] = 1,
        ["Infrastructure/Persistence/Seed/KlacksyKnowledgeMemorySeed.cs"] = 1,
        ["Infrastructure/Plugins/MessagingPluginOfflineMessengerNotifier.cs"] = 1,
        ["Infrastructure/Scripting/ScriptExecutionContext.cs"] = 4,
        ["Infrastructure/Services/Archives/ZipArchiveLimitGuard.cs"] = 1,
        ["Infrastructure/Services/Assistant/KlacksSelfApiClient.cs"] = 1,
        ["Infrastructure/Services/Assistant/PhoneticConfigProvider.cs"] = 1,
        ["Infrastructure/Services/Assistant/Providers/Anthropic/AnthropicProvider.cs"] = 1,
        ["Infrastructure/Services/Assistant/Providers/Base/BaseHttpProvider.cs"] = 1,
        ["Infrastructure/Services/Exports/PayrollArtifactStorage.cs"] = 1,
        ["Infrastructure/Services/Imports/FileSystemObjectStorageService.cs"] = 5,
        ["Infrastructure/Services/Imports/XmlOrderImportParser.cs"] = 1,
        ["Infrastructure/Services/Macros/MacroScriptRunner.cs"] = 1,
        ["Infrastructure/Services/Plugins/PluginArchiveManifestCheck.cs"] = 1,
        ["Infrastructure/Services/Schedules/HarmonizerJobRunner.cs"] = 4,
        ["Infrastructure/Services/Schedules/HolisticHarmonizer/HolisticHarmonizerJobRunner.cs"] = 4,
        ["Infrastructure/Services/Schedules/WizardJobRunner.cs"] = 4,
        ["Infrastructure/StartupChecks/PlatformDependencyHealthCheck.cs"] = 1,
        ["Infrastructure/WebSearch/SerperWebSearchProvider.cs"] = 1,
        ["Infrastructure/WebSearch/TavilyWebSearchProvider.cs"] = 1,
        ["Presentation/Controllers/Assistant/ChatController.cs"] = 1,
        ["Presentation/Controllers/Assistant/TtsController.cs"] = 1,
        ["Presentation/Controllers/UserBackend/Email/ReceivedEmailController.cs"] = 1,
        ["Presentation/Controllers/UserBackend/Settings/GeneralSettingsController.cs"] = 1,
        ["Presentation/Filters/IdempotentRequestFilter.cs"] = 1,
    };
}
