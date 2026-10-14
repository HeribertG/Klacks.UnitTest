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
/// CanEditSchedule alone, part of the Planer floor); confirm_break and unconfirm_break mirror BreaksController Confirm and
/// Unconfirm, which carry no role attribute either (the lock-level rules inside the handler decide by role).
/// A third direction covers the Supervisor: the messaging skills send_message and read_messages mirror
/// MessagingController (Admin plus Authorised, group-visibility scoped inside the skill) and the e-mail inbox skills
/// mirror ReceivedEmailController (Admin plus Authorised, group-visibility scoped inside the handlers); both carry
/// CanEditClients, the one granular right whose role set is exactly Admin plus Authorised and outside the Planer floor,
/// so a caller without a role stays out. list_messaging_providers mirrors the provider GET that every signed-in user
/// may call (CanViewClients, part of the floor), while diagnose_messaging_setup, create_email_folder and
/// delete_email_folder mirror Admin-only routes (MessagingSetupController, EmailFoldersController, gate
/// CanEditSettings). The messaging plugin seeds are read next to the core seeds.
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
    private const string MessagingPluginDirectory = "messaging";

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
        ["create_test_environment"] = Permissions.CanEditSettings,
        ["diagnose_messaging_setup"] = Permissions.CanEditSettings,
        ["create_email_folder"] = Permissions.CanEditSettings,
        ["delete_email_folder"] = Permissions.CanEditSettings
    };

    private static readonly Dictionary<string, string> PlannerFloorGateBySkill = new(StringComparer.Ordinal)
    {
        ["add_client_note"] = Permissions.CanEditClientNotes,
        ["update_annotation"] = Permissions.CanEditClientNotes,
        ["delete_annotation"] = Permissions.CanEditClientNotes,
        ["restore_work"] = Permissions.CanEditSchedule,
        ["reassign_work"] = Permissions.CanEditSchedule,
        ["bulk_delete_works"] = Permissions.CanEditSchedule,
        ["confirm_break"] = Permissions.CanEditSchedule,
        ["unconfirm_break"] = Permissions.CanEditSchedule,
        ["list_messaging_providers"] = Permissions.CanViewClients
    };

    private static readonly Dictionary<string, string> SupervisorGateBySkill = new(StringComparer.Ordinal)
    {
        ["send_message"] = Permissions.CanEditClients,
        ["read_messages"] = Permissions.CanEditClients,
        ["list_email_folders"] = Permissions.CanEditClients,
        ["list_emails"] = Permissions.CanEditClients,
        ["read_email"] = Permissions.CanEditClients,
        ["mark_email_read"] = Permissions.CanEditClients,
        ["fetch_new_emails"] = Permissions.CanEditClients,
        ["move_email_to_folder"] = Permissions.CanEditClients,
        ["delete_email"] = Permissions.CanEditClients,
        ["restore_email"] = Permissions.CanEditClients,
        ["get_email_analysis"] = Permissions.CanEditClients,
        ["translate_email"] = Permissions.CanEditClients,
        ["list_emails_by_client"] = Permissions.CanEditClients,
        ["list_emails_by_group"] = Permissions.CanEditClients,
        ["get_email_group_tree"] = Permissions.CanEditClients
    };

    private static readonly string[] SupervisorSkillsIndependentOfGroupVisibility =
    [
        "list_email_folders",
        "fetch_new_emails"
    ];

    private static readonly string[] MessagingAndEmailNameMarkers = ["email", "messag", "messeng"];

    private static readonly string[] InboundAnalysisSkills =
    [
        "get_messenger_analysis",
        "close_clarification"
    ];

    private static readonly string[] SupervisorReachableSkillsOutsideTheInbox =
    [
        "add_client_email"
    ];

    private static readonly Lazy<Dictionary<string, SkillSeedDefinition>> AllSeeds = new(LoadSeeds);

    private static IEnumerable<string> AdminOnlySkills => AdminOnlyGateBySkill.Keys;

    private static IEnumerable<string> PlannerFloorSkills => PlannerFloorGateBySkill.Keys;

    private static IEnumerable<string> SupervisorSkills => SupervisorGateBySkill.Keys;

    private static Dictionary<string, SkillSeedDefinition> Seeds() => AllSeeds.Value;

    private static Dictionary<string, SkillSeedDefinition> LoadSeeds()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var corePath = RepositoryRootLocator.FindFile("Klacks.Api", "Application", "Skills", "Definitions", SeedFileName);
        corePath.ShouldNotBeNull();
        var core = JsonSerializer.Deserialize<SkillSeedFile>(File.ReadAllText(corePath), options);

        var pluginPath = RepositoryRootLocator.FindFile("Klacks.Api", "Plugins", "Features", MessagingPluginDirectory, SeedFileName);
        pluginPath.ShouldNotBeNull();
        var plugin = JsonSerializer.Deserialize<List<SkillSeedDefinition>>(File.ReadAllText(pluginPath), options);

        return core!.Skills.Concat(plugin!).ToDictionary(s => s.Name, StringComparer.Ordinal);
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

    [TestCaseSource(nameof(SupervisorSkills))]
    public void SkillMirroringASupervisorEndpoint_CarriesExactlyTheClientEditGate(string skill)
    {
        Seeds()[skill].RequiredPermissions.ShouldBe(
            new List<string> { SupervisorGateBySkill[skill] },
            $"'{skill}' sends the same request as a REST action for Admin plus Authorised (MessagingController, " +
            "ReceivedEmailController). CanEditClients is the one right with exactly that role set; CanViewSettings " +
            "would lock the Supervisor out, CanManageAutomation names a different feature. Remember the version bump " +
            "when changing it.");
    }

    [TestCaseSource(nameof(SupervisorSkills))]
    public void SupervisorAndAdmin_ReachASupervisorSkill_ThePlanerFloorDoesNot(string skill)
    {
        MayRun(skill).ShouldBeFalse($"The Planer floor must not reach '{skill}': the REST action is closed to a caller without a role.");
        MayRun(skill, Roles.Authorised).ShouldBeTrue($"A Supervisor must reach '{skill}'.");
        MayRun(skill, Roles.Admin).ShouldBeTrue($"Admin must reach '{skill}'.");
    }

    [Test]
    public void SupervisorGateList_NamesEveryMessagingAndEmailSeedTheSupervisorReaches()
    {
        SupervisorReachableMessagingAndEmailSkills()
            .Except(InboundAnalysisSkills, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
                SupervisorGateBySkill.Keys.OrderBy(n => n, StringComparer.Ordinal),
                "A messaging or e-mail seed that a Supervisor reaches and the Planer floor does not must be pinned in " +
                "SupervisorGateBySkill (and so reach the group-visibility partition below). add_client_email edits " +
                "client master data and is named outside the inbox on purpose; the inbound-analysis skills keep " +
                "CanManageAutomation, pinned by InboundAnalysisSkillPermissionTests.");
    }

    [Test]
    public void EverySupervisorGatedMessagingAndEmailSkill_IsEitherGroupVisibilityBound_OrNamedIndependentOfIt()
    {
        var bound = GroupVisibilityBoundSkills.All.ToHashSet(StringComparer.Ordinal);
        var independent = SupervisorSkillsIndependentOfGroupVisibility.ToHashSet(StringComparer.Ordinal);
        var covered = bound.Concat(independent).ToHashSet(StringComparer.Ordinal);

        bound.Intersect(independent).ShouldBeEmpty();
        SupervisorReachableMessagingAndEmailSkills()
            .Concat(InboundAnalysisSkills)
            .Where(skill => !covered.Contains(skill))
            .ShouldBeEmpty(
            "A Supervisor-gated messaging or e-mail skill reads or changes client-owned data under the group visibility of " +
            "the caller. List it in GroupVisibilityBoundSkills so the executor refuses it without an identified user, or " +
            "name it here as independent of group visibility (folder counts and the mailbox fetch touch no client).");
    }

    private static IEnumerable<string> SupervisorReachableMessagingAndEmailSkills() =>
        Seeds().Keys
            .Where(name => MessagingAndEmailNameMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal)))
            .Where(name => MayRun(name, Roles.Authorised) && !MayRun(name))
            .Except(SupervisorReachableSkillsOutsideTheInbox, StringComparer.Ordinal);

    [Test]
    public void ClientNoteGate_IsPartOfThePlanerFloor()
    {
        Permissions.PlannerFloor.ShouldContain(Permissions.CanEditClientNotes);
    }
}
