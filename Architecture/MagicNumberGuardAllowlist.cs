// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shrink-only baseline of MagicNumberGuardTests: "path relative to Klacks.Api -> number of magic-number
/// literals the file had when it was listed". An entry is a ceiling AND a floor - a file above its number
/// fails, and so does a file below it, so every cleanup lowers (or removes) its entry in the same change.
/// Never add an entry and never raise a number: name the value as a constant instead.
///
/// Start (2026-10-23): 647 literals in 175 files on Api 669bcab18; the change that introduced the guard
/// named 165 of them (28 files), leaving 482 in 147 files. ClientFilterService (1) then moved from Domain to
/// Infrastructure, out of the guarded directories, leaving 481 in 146 files. The largest remaining groups are deliberate for
/// now: calendar algorithms (HolidaysListCalculator, HijriCalendar), sample and evaluation data
/// (ExportSampleDataFactory, HarmonizerEvalScenarioFactory, CreateTestEnvironmentSkill), option and DTO
/// defaults in property initializers, the feature-vector slots of TurnIntentHandFeatures and the Haversine
/// formula coefficients.
/// </summary>

namespace Klacks.UnitTest.Architecture;

public static class MagicNumberGuardAllowlist
{
    public static readonly IReadOnlyDictionary<string, int> Entries = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Application/Commands/Assistant/CreateProviderCommand.cs"] = 1,
        ["Application/Configuration/BackgroundServiceOptions.cs"] = 10,
        ["Application/Configuration/SelfApiOptions.cs"] = 1,
        ["Application/DTOs/Assistant/ExportUsageDataRequest.cs"] = 1,
        ["Application/DTOs/Assistant/GetConversationHistoryRequest.cs"] = 1,
        ["Application/DTOs/Filter/ClientAvailabilityClientFilter.cs"] = 1,
        ["Application/DTOs/Filter/WorkScheduleFilter.cs"] = 2,
        ["Application/DTOs/Reports/FieldStyleResource.cs"] = 1,
        ["Application/DTOs/Reports/ReportFieldResource.cs"] = 2,
        ["Application/DTOs/Reports/ReportMarginsResource.cs"] = 4,
        ["Application/DTOs/Schedules/ClientWorkResource.cs"] = 1,
        ["Application/DTOs/Update/UpdateConfig.cs"] = 2,
        ["Application/Handlers/Assistant/GetAgentSessionsQueryHandler.cs"] = 1,
        ["Application/Interfaces/IClientSearchRepository.cs"] = 2,
        ["Application/Interfaces/IGroupSearchRepository.cs"] = 1,
        ["Application/Interfaces/IShiftSearchRepository.cs"] = 1,
        ["Application/Mappers/FilterMapper.cs"] = 2,
        ["Application/Queries/Assistant/GetGlobalRulesQuery.cs"] = 1,
        ["Application/Queries/Assistant/GetLLMUsageQuery.cs"] = 1,
        ["Application/Queries/Assistant/GetSoulHistoryQuery.cs"] = 1,
        ["Application/Services/Assistant/Evaluation/TurnEval/TurnEvalBaseline.cs"] = 3,
        ["Application/Services/Assistant/Evaluation/TurnEval/TurnEvalMemoryProbe.cs"] = 1,
        ["Application/Services/ClientImport/ClientImportColumnDetector.cs"] = 1,
        ["Application/Services/Exports/ExportSampleDataFactory.cs"] = 39,
        ["Application/Services/Schedules/HarmonizerResultCache.cs"] = 1,
        ["Application/Services/Schedules/HolisticHarmonizer/HarmonizerEvalScenarioFactory.cs"] = 7,
        ["Application/Services/Schedules/JobTerminalStateCache.cs"] = 1,
        ["Application/Services/Schedules/PlanningRules/PlanningRuleCarryInLoader.cs"] = 1,
        ["Application/Services/Schedules/WizardContextBuilder.cs"] = 1,
        ["Application/Services/Schedules/WizardResultCache.cs"] = 1,
        ["Application/Skills/AddBreakSkill.cs"] = 3,
        ["Application/Skills/AddClientToNearestGroupSkill.cs"] = 3,
        ["Application/Skills/BundleNearbyTimeRangeShiftsIntoContainerSkill.cs"] = 6,
        ["Application/Skills/CheckAbsenceCapacityReserveSkill.cs"] = 2,
        ["Application/Skills/CreateShiftSkill.cs"] = 11,
        ["Application/Skills/CreateTestEnvironmentSkill.cs"] = 44,
        ["Application/Skills/CutShiftSkill.cs"] = 5,
        ["Application/Skills/GetGroupAbsenceOverlapSkill.cs"] = 2,
        ["Application/Skills/InterpretResourceMonitorSkill.cs"] = 1,
        ["Application/Skills/UpdateAbsenceTypeSkill.cs"] = 1,
        ["Application/Skills/UpdateWorkSkill.cs"] = 1,
        ["Domain/DTOs/Filter/ShiftScheduleFilter.cs"] = 1,
        ["Domain/Interfaces/Assistant/IAgentMemoryRepository.cs"] = 2,
        ["Domain/Interfaces/Assistant/IAgentSessionRepository.cs"] = 2,
        ["Domain/Interfaces/Assistant/IAgentSoulRepository.cs"] = 1,
        ["Domain/Interfaces/Assistant/IGlobalAgentRuleRepository.cs"] = 1,
        ["Domain/Interfaces/Assistant/ILLMRepository.cs"] = 2,
        ["Domain/Interfaces/Assistant/IOpenMeteoClient.cs"] = 1,
        ["Domain/Interfaces/RouteOptimization/IGeocodingService.cs"] = 1,
        ["Domain/Models/Assistant/ContextBudgetProfile.cs"] = 1,
        ["Domain/Models/Assistant/LLMModel.cs"] = 2,
        ["Domain/Models/Assistant/PhoneticConfig.cs"] = 2,
        ["Domain/Models/Assistant/SttConfig.cs"] = 1,
        ["Domain/Models/Criteria/BaseCriteria.cs"] = 1,
        ["Domain/Models/Filters/ClientBaseFilter.cs"] = 1,
        ["Domain/Models/Filters/PaginationParams.cs"] = 1,
        ["Domain/Models/Filters/WorkFilter.cs"] = 2,
        ["Domain/Models/Reports/FieldStyle.cs"] = 1,
        ["Domain/Models/Reports/ReportField.cs"] = 2,
        ["Domain/Models/Reports/ReportMargins.cs"] = 4,
        ["Domain/Models/Schedules/ClientScheduleDetail.cs"] = 1,
        ["Domain/Services/Assistant/BookingDiagnosisIntentResolver.cs"] = 1,
        ["Domain/Services/Assistant/ConversationHistoryBudget.cs"] = 1,
        ["Domain/Services/Assistant/Grounding/ToolResultGroundingPoolBuilder.cs"] = 1,
        ["Domain/Services/Assistant/Providers/ILLMProvider.cs"] = 1,
        ["Domain/Services/Assistant/Providers/LLMProviderRequest.cs"] = 2,
        ["Domain/Services/Assistant/RecipeInitialSlotExtractor.cs"] = 4,
        ["Domain/Services/Assistant/Skills/SkillDateCultureResolver.cs"] = 1,
        ["Domain/Services/Assistant/TurnIntentHandFeatures.cs"] = 7,
        ["Domain/Services/Assistant/TurnIntentLocaleGate.cs"] = 2,
        ["Domain/Services/ContainerTemplates/ContainerAvailableTasksService.cs"] = 1,
        ["Domain/Services/Geo/HaversineDistanceCalculator.cs"] = 6,
        ["Domain/Services/Holidays/HijriCalendar.cs"] = 52,
        ["Domain/Services/Holidays/HolidaysListCalculator.cs"] = 76,
    };
}
