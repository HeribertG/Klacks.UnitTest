// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shrink-only baseline of GroupItemMembershipPredicateGuardTests: "path relative to Klacks.Api -> number of hand-written
/// token tests in client-membership queries on the group items". An entry is a ceiling AND a floor: a file above its
/// number fails, and so does a file below it, so every cleanup lowers (or removes) its entry in the same change. Never
/// add an entry and never raise a number: name the predicate of RegularMembership the reader means instead.
///
/// Reviewed when the guard came in (2026-11-11, temporary membership per day): the listed queries overlay the rows of
/// one scenario on the real plan in a single test, which none of the named predicates expresses.
///
/// A green guard does NOT mean every reader of memberships has chosen. The scanner only sees queries that start at the
/// group item set. These readers test the token by hand behind a navigation or on loaded rows, are invisible to it, and
/// their choice between "regular" and "every real membership" is still open (state 2026-11-11):
/// - Domain/Services/Clients/ClientGroupVisibilityFilter (who a restricted caller may see): takes temporary rows along,
///   date-blind.
/// - Infrastructure/Services/Clients/ClientGroupFilterService.FilterClientsByGroupId, the "without group" filter and the
///   group filter of the employee list (lines 52 and 77 at that state): take temporary rows along, date-blind. For the
///   group filter that is wanted for now (the employee list keeps showing a borrowed person under the group); whether it
///   should follow the borrowed days is open.
/// - Infrastructure/Repositories/Associations/GroupItemRepository.GetVisibilityMembershipAsync: takes temporary rows
///   along, date-blind.
/// - Application/Handlers/Groups/GroupUngroupedByCityNameCommandHandler and PartitionClientsByAddressCommandHandler, and
///   the planners that work on loaded client.GroupItems (GroupPartitionPlanner, CustomerGroupingPlanner,
///   QualificationGroupPlanner, ShiftCityGroupPlanner, AnalyzeGroupSemanticsSkill, DiagnosePlanningPeriodSkill): they
///   filter rows somebody else loaded, so what they see follows the loader. The by-type loads and the single-client
///   loads of ClientRepository include regular memberships only; a caller that hands them rows from another loader
///   gets the temporary ones too.
/// - Infrastructure/Services/Groups/GroupClientService.GetClientIdsForGroups tests no token at all and so also counts
///   the memberships scenarios hold; its callers ask whom a plan change concerns (notifications, recalculation).
///
/// Blind spot of the scanner: it counts hand-written token tests, so a reader that tests no token at all is never
/// reported, whatever it takes along. These readers take every group item, scenario rows and the temporary rows of
/// borrowed days included, date-blind, and their choice is open (state 2026-11-12, owner decision on visibility
/// pending, not changed with the guard):
/// - Infrastructure/Repositories/Associations/GroupPeriodRangeQuery.LoadAsync (group period of the schedule grid: the
///   earliest start to the latest end over all items, so the days between two borrowed days are open).
/// - Infrastructure/Repositories/Staffs/ClientFilterRepository, the Include(GroupItems) of the filtered client list.
/// - Infrastructure/Services/Clients/ClientBaseQueryService.GetPageWithGroupItemsAsync, Include(GroupItems).
/// - Infrastructure/Repositories/Staffs/ClientSearchRepository, Include(GroupItems) and the GroupIds projection.
/// - Infrastructure/Repositories/Staffs/ClientBreakPlaceholderRepository, both Include(GroupItems).
/// - Infrastructure/Repositories/Associations/GroupItemRepository.GetGroupTreeIdsForClientAsync.
/// - Infrastructure/Repositories/Staffs/ClientRepository.GetActiveClientsWithAddressesForGroupsAsync (the agents of the
///   automatic next-period autofill; the start of the run drops the borrowed people, scenario memberships still count).
///
/// Decided "regular members only", also outside what the scanner sees (2026-11-11): the planning runs of a group plan
/// its regular members. The agents of the planning skills (GroupPlanningAgentRepository through
/// ClientGroupFilterService.FilterRegularMembersOfGroup, a navigation filter built from RegularMembership), the agents
/// of Wizard 4 (Wizard4AgentResolver) and the people a source scenario covers in a recovery run
/// (RecoverySnapshotBuilder through GetRealClientIdsFromGroupAndSubgroups) read regular memberships. The agent ids a
/// run started from the user interface (or with explicit agents) sends lose, at the start of the run, the people who
/// hold only temporary memberships in the subtree of the run's group (IBorrowedAgentFilter through
/// GroupItemRepository.GetBorrowedOnlyClientIdsAsync); everybody else the client sent stays.
/// </summary>

namespace Klacks.UnitTest.Architecture;

public static class GroupItemMembershipPredicateGuardAllowlist
{
    public static readonly IReadOnlyDictionary<string, int> Entries = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // Real memberships plus those of the asked scenario, each tested against the day range by its own validity.
        ["Infrastructure/Repositories/Scheduling/PlanningRuleDataReader.cs"] = 1,

        // Agents of a scenario summary: real members plus the people the scenario borrowed, not bound to its period.
        ["Infrastructure/Services/Schedules/ScenarioSummaryBuilder.cs"] = 1,
    };
}
