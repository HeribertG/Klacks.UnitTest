// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shrink-only baseline of GroupItemShiftTokenGuardTests: "path relative to Klacks.Api -> number of group item queries that
/// reach a shift without looking at AnalyseToken". An entry is a ceiling AND a floor: a file above its number fails, and so
/// does a file below it, so every cleanup lowers (or removes) its entry in the same change. Never add an entry and never
/// raise a number: filter the query instead.
///
/// Reviewed when the guard came in (2026-11-01, virtual shift keys): a listed query takes a shift id the caller already
/// holds (write paths, group lookups by shift) or hands on ids or group ids, which the readers named per entry filter on
/// their side.
/// </summary>

namespace Klacks.UnitTest.Architecture;

public static class GroupItemShiftTokenGuardAllowlist
{
    public static readonly IReadOnlyDictionary<string, int> Entries = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // FindShiftLinkIdAsync and GetGroupIdsByShiftId take a known shift id.
        ["Infrastructure/Repositories/Associations/GroupItemRepository.cs"] = 2,

        // Shift ids of a group; the dashboard and e-mail readers use them on works and shifts with AnalyseToken == null.
        ["Infrastructure/Repositories/Dashboard/ResourceMonitorReadRepository.cs"] = 2,

        // Shift-to-group pairs; the coverage statistics join them to the shifts they read.
        ["Infrastructure/Repositories/Dashboard/ShiftCoverageReadRepository.cs"] = 1,

        // HasWorks* subqueries inside a Work query that filters AnalyseToken == null.
        ["Infrastructure/Repositories/Schedules/ShiftRepository.cs"] = 3,

        // GetActiveGroupItemsAsync: the stored links of one known shift, for the write guard.
        ["Infrastructure/Repositories/Schedules/ShiftVisibilityReadRepository.cs"] = 1,

        // Group filters inside Work queries that filter AnalyseToken == null.
        ["Infrastructure/Repositories/Schedules/WorkRepository.cs"] = 4,

        // Soft delete of the links of the shifts of one scenario.
        ["Infrastructure/Services/AnalyseScenarios/ScenarioDataDiscarder.cs"] = 1,

        // Group tags of known shift ids that the caller read from its own world.
        ["Infrastructure/Services/Schedules/RestrictedTimeWindowEvaluator.cs"] = 1,
        ["Infrastructure/Services/Schedules/WizardRestrictedWindowBuilder.cs"] = 1,

        // Group link write of one known shift.
        ["Infrastructure/Services/Shifts/ShiftGroupManagementService.cs"] = 2,
    };
}
