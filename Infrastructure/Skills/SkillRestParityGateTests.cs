// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seed gates of skills that send the same mediator request as an Admin-only REST action, so Klacksy never
/// lets a Supervisor or a Planer do what the endpoint refuses them: the qualification master data
/// (QualificationController is Admin-only, gate CanEditSettings), the deletion of client sub-resources
/// (InputBaseController.Delete is Admin-only, gate CanDeleteClients), the proactive governance write
/// (ProactiveGovernanceController is Admin-only, gate CanEditSettings) and the period-closing reads
/// (PeriodClosingController Issues/AuditLog/UsedPeriods and the period-closing page are Admin-only, gate Admin) and
/// create_test_environment, which writes demo employees, a contract and shifts into the live installation (gate
/// CanEditSettings). The opposite direction is pinned too: the client-note skills mirror AnnotationsController, whose
/// writes need CanEditClientNotes alone (part of the Planer floor), so the Planer who may write a note in the UI may
/// also ask Klacksy to; and the grid-intent skills restore_work, reassign_work and bulk_delete_works mirror the
/// WorksController endpoints Restore, ReassignClient and the bulk DELETE, which carry no role attribute (gate
/// CanEditSchedule alone, part of the Planer floor).
/// The rule applied is SkillExecutorService.ValidatePermissions: the Admin role passes, everyone else needs every
/// required right among Permissions.ExpandRoles of their roles.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillRestParityGateTests
{
    private const string SeedFileName = "skill-seeds.json";

    private static readonly Dictionary<string, string> AdminOnlyGateBySkill = new(StringComparer.Ordinal)
    {
        ["create_qualification"] = Permissions.CanEditSettings,
        ["update_qualification"] = Permissions.CanEditSettings,
        ["delete_qualification"] = Permissions.CanEditSettings,
        ["set_proactive_governance"] = Permissions.CanEditSettings,
        ["delete_address"] = Permissions.CanDeleteClients,
        ["delete_communication"] = Permissions.CanDeleteClients,
        ["delete_membership"] = Permissions.CanDeleteClients,
        ["list_period_issues"] = Roles.Admin,
        ["list_period_audit_log"] = Roles.Admin,
        ["list_open_periods"] = Roles.Admin,
        ["create_test_environment"] = Permissions.CanEditSettings
    };

    private static readonly Dictionary<string, string> PlannerFloorGateBySkill = new(StringComparer.Ordinal)
    {
        ["add_client_note"] = Permissions.CanEditClientNotes,
        ["update_annotation"] = Permissions.CanEditClientNotes,
        ["delete_annotation"] = Permissions.CanEditClientNotes,
        ["restore_work"] = Permissions.CanEditSchedule,
        ["reassign_work"] = Permissions.CanEditSchedule,
        ["bulk_delete_works"] = Permissions.CanEditSchedule
    };

    private static IEnumerable<string> AdminOnlySkills => AdminOnlyGateBySkill.Keys;

    private static IEnumerable<string> PlannerFloorSkills => PlannerFloorGateBySkill.Keys;

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

    [TestCaseSource(nameof(AdminOnlySkills))]
    public void SkillMirroringAnAdminOnlyEndpoint_CarriesItsAdminOnlyGate(string skill)
    {
        Seeds()[skill].RequiredPermissions.ShouldBe(
            new List<string> { AdminOnlyGateBySkill[skill] },
            $"'{skill}' sends the same request as an Admin-only REST action. Remember the version bump when " +
            "changing its gate: SkillSeedLoader skips a seed whose version did not increase.");
    }

    [TestCaseSource(nameof(AdminOnlySkills))]
    public void PlannerAndSupervisor_AreRefused_AdminPasses(string skill)
    {
        MayRun(skill).ShouldBeFalse($"The Planer floor must not reach '{skill}'.");
        MayRun(skill, Roles.Authorised).ShouldBeFalse($"A Supervisor must not reach '{skill}'.");
        MayRun(skill, Roles.Admin).ShouldBeTrue($"Admin must keep reaching '{skill}'.");
    }

    [TestCaseSource(nameof(PlannerFloorSkills))]
    public void SkillMirroringAPlannerFloorEndpoint_CarriesExactlyItsFloorGate(string skill)
    {
        Seeds()[skill].RequiredPermissions.ShouldBe(
            new List<string> { PlannerFloorGateBySkill[skill] },
            $"'{skill}' sends the same request as a REST action gated on {PlannerFloorGateBySkill[skill]} alone " +
            "(AnnotationsController, WorksController). A stricter skill gate such as CanEditClients locks out the " +
            "Planer the UI lets do it. Remember the version bump when changing it.");
    }

    [TestCaseSource(nameof(PlannerFloorSkills))]
    public void PlannerSupervisorAndAdmin_AllReachAPlannerFloorSkill(string skill)
    {
        MayRun(skill).ShouldBeTrue($"The Planer floor must reach '{skill}', as it reaches the UI action.");
        MayRun(skill, Roles.Authorised).ShouldBeTrue($"A Supervisor must reach '{skill}'.");
        MayRun(skill, Roles.Admin).ShouldBeTrue($"Admin must reach '{skill}'.");
    }

    [Test]
    public void ClientNoteGate_IsPartOfThePlanerFloor()
    {
        Permissions.PlannerFloor.ShouldContain(Permissions.CanEditClientNotes);
    }
}
