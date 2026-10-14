// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The shipped skill seeds follow the UI/API rights model (Option B): schedule-only write skills are open to the planner
/// floor (CanEditSchedule) exactly like the REST endpoints they mirror (WorksController work CRUD and confirm, Breaks,
/// WorkChanges, BreakPlaceholders, Recovery CoverAbsence, Expenses, ScheduleNotes and ScheduleCommands carry no role attribute);
/// shift master data stays with CanEditShifts; day approval is Admin and Supervisor;
/// closing and reopening a period is Admin only, like WorksController.ClosePeriod/ReopenPeriod (the Admin-only
/// period-closing reads are pinned in SkillRestParityGateTests). The permission rule is
/// the one SkillExecutorService.ValidatePermissions applies: the Admin role passes, everyone else needs every required
/// right among the rights Permissions.ExpandRoles yields for the caller's roles.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScheduleSkillPermissionAlignmentTests
{
    private const string SeedFileName = "skill-seeds.json";

    private static readonly string[] FloorScheduleSkills =
    [
        "place_work", "update_work", "delete_work", "restore_work", "reassign_work", "bulk_delete_works",
        "confirm_work", "unconfirm_work", "confirm_break", "unconfirm_break",
        "add_break", "update_break", "delete_break", "bulk_add_absence_for_group",
        "add_workchange", "update_workchange", "delete_workchange",
        "add_break_placeholder", "update_break_placeholder", "delete_break_placeholder",
        "propose_plan", "cover_absence", "add_expense", "update_expense", "delete_expense",
        "add_schedule_note", "update_schedule_note", "delete_schedule_note",
        "add_schedule_command", "add_schedule_commands_range"
    ];

    private static readonly string[] SupervisorScheduleSkills =
    [
        "approve_day", "revoke_day_approval"
    ];

    private static readonly string[] ShiftMasterDataSkills =
    [
        "create_shift", "update_shift", "cut_shift", "cut_shift_by_date", "reset_shift_cuts", "seal_shift",
        "create_container_template", "set_shift_preferences", "add_shift_to_group",
        "assign_orders_to_groups", "find_split_shift_candidates"
    ];

    private static readonly string[] AdminOnlyPeriodSkills = ["close_period", "reopen_period"];

    private static Dictionary<string, SkillSeedDefinition> Seeds()
    {
        var path = RepositoryRootLocator.FindFile("Klacks.Api", "Application", "Skills", "Definitions", SeedFileName);
        path.ShouldNotBeNull();
        var seed = JsonSerializer.Deserialize<SkillSeedFile>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return seed!.Skills.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    private static bool MayRun(string skill, params string[] roles)
    {
        var rights = Permissions.ExpandRoles(roles);
        return rights.Contains(Roles.Admin) || Seeds()[skill].RequiredPermissions.All(rights.Contains);
    }

    [TestCaseSource(nameof(FloorScheduleSkills))]
    public void FloorOnlyUser_MayRunScheduleOnlyWriteSkills(string skill)
    {
        MayRun(skill).ShouldBeTrue();
        Seeds()[skill].RequiredPermissions.ShouldContain(Permissions.CanEditSchedule);
        Seeds()[skill].RequiredPermissions.ShouldNotContain(Permissions.CanEditShifts);
    }

    [TestCaseSource(nameof(SupervisorScheduleSkills))]
    public void SkillsWithAStricterRestEndpoint_AreSupervisorAndAdminOnly(string skill)
    {
        MayRun(skill).ShouldBeFalse();
        MayRun(skill, Roles.Authorised).ShouldBeTrue();
        MayRun(skill, Roles.Admin).ShouldBeTrue();
    }

    [TestCaseSource(nameof(ShiftMasterDataSkills))]
    public void FloorOnlyUser_MayNotRunShiftMasterDataSkills(string skill)
    {
        MayRun(skill).ShouldBeFalse();
        MayRun(skill, Roles.Authorised).ShouldBeTrue();
    }

    [TestCaseSource(nameof(AdminOnlyPeriodSkills))]
    public void ClosingAndReopeningAPeriod_IsAdminOnly(string skill)
    {
        MayRun(skill).ShouldBeFalse();
        MayRun(skill, Roles.Authorised).ShouldBeFalse();
        MayRun(skill, Roles.Admin).ShouldBeTrue();
    }
}
