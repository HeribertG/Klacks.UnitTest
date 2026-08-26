// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UnattendedSkillPolicy — the fail-closed gate in front of every background skill run.
/// It runs against the REAL SkillRiskClassifier so a skill moving in or out of the sensitive list is
/// covered here too, and only the registry is substituted. The matrix is deliberately stricter than the
/// interactive one: reversible skills need Autonomous, scenario-gated skills need Assisted, and an
/// irreversible skill runs only on a scheduled task that explicitly opted in — never on the heartbeat.
/// Every refusal text is checked to state the CAUSE and the REMEDY and to claim no consequence: the
/// policy serves the scheduled runner and the proactive heartbeat alike, and only the caller knows
/// whether a task was paused, disabled, or — on the heartbeat — never existed to begin with.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Scheduling;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Assistant.Scheduling;

[TestFixture]
public class UnattendedSkillPolicyTests
{
    private const string ReadOnlySkill = "list_clients";
    private const string ReversibleSkill = "delete_work";
    private const string ScenarioGatedSkill = "cover_absence";
    private const string IrreversibleSkill = "update_client";
    private const string SensitiveSkill = "delete_client";

    private ISkillRegistry _registry = null!;
    private UnattendedSkillPolicy _policy = null!;

    private static readonly IReadOnlyList<string> OwnerPermissions =
        new[] { "CanViewClients", "CanEditClients" };

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _policy = new UnattendedSkillPolicy(_registry, new SkillRiskClassifier());
    }

    private void Known(string name, SkillCategory category = SkillCategory.Crud)
    {
        _registry.GetSkillByName(name).Returns(new SkillDescriptor(
            name,
            "test skill",
            category,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null));
    }

    private static UnattendedSkillRequest Request(
        string skillName,
        IReadOnlyList<string>? ownerPermissions = null,
        AutonomyLevel autonomyLevel = AutonomyLevel.Autonomous,
        UnattendedExecutionKind executionKind = UnattendedExecutionKind.ScheduledTask,
        bool allowIrreversibleUnattended = false) =>
        new(skillName, ownerPermissions ?? OwnerPermissions, autonomyLevel, executionKind, allowIrreversibleUnattended);

    [Test]
    public void Decide_EmptyOwnerPermissions_IsDenied()
    {
        Known(IrreversibleSkill);

        var decision = _policy.Decide(Request(IrreversibleSkill, Array.Empty<string>()));

        decision.Allowed.ShouldBeFalse();
        decision.Reason!.ShouldContain("no permissions at all");
        decision.DenyReason.ShouldBe(UnattendedDenyReason.NoPermissions);
    }

    [Test]
    public void Decide_EmptyOwnerPermissions_IsDeniedBeforeTheRegistryIsAsked()
    {
        _policy.Decide(Request(IrreversibleSkill, Array.Empty<string>()));

        _registry.DidNotReceive().GetSkillByName(Arg.Any<string>());
    }

    [Test]
    public void Decide_UnknownSkill_IsDenied()
    {
        _registry.GetSkillByName("vanished_skill").Returns((SkillDescriptor?)null);

        var decision = _policy.Decide(Request("vanished_skill"));

        decision.Allowed.ShouldBeFalse();
        decision.Reason!.ShouldContain("no longer exists");
        decision.DenyReason.ShouldBe(UnattendedDenyReason.UnknownSkill);
    }

    [Test]
    public void Decide_SensitiveSkill_IsDenied()
    {
        Known(SensitiveSkill);

        var decision = _policy.Decide(Request(SensitiveSkill));

        decision.Allowed.ShouldBeFalse();
        decision.Reason!.ShouldContain("sensitive");
        decision.DenyReason.ShouldBe(UnattendedDenyReason.SensitiveSkill);
    }

    [Test]
    public void Decide_SensitiveSkill_StaysDeniedAtTheHighestLevelEvenWithTheOptIn()
    {
        Known(SensitiveSkill);

        var decision = _policy.Decide(Request(
            SensitiveSkill,
            autonomyLevel: AutonomyLevel.FullyAutonomous,
            allowIrreversibleUnattended: true));

        decision.Allowed.ShouldBeFalse();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.SensitiveSkill);
    }

    [Test]
    public void Decide_IrreversibleSkill_WithoutOptIn_IsDenied()
    {
        Known(IrreversibleSkill);

        var decision = _policy.Decide(Request(IrreversibleSkill));

        decision.Allowed.ShouldBeFalse();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.IrreversibleWithoutOptIn);
        decision.Reason!.ShouldContain("explicitly opts in");
    }

    [Test]
    public void Decide_EveryRefusal_StatesTheCauseWithoutClaimingAConsequence()
    {
        Known(IrreversibleSkill);
        Known(SensitiveSkill);
        Known(ReversibleSkill);
        _registry.GetSkillByName("vanished_skill").Returns((SkillDescriptor?)null);

        var refusals = new[]
        {
            _policy.Decide(Request(IrreversibleSkill, Array.Empty<string>())),
            _policy.Decide(Request("vanished_skill")),
            _policy.Decide(Request(SensitiveSkill)),
            _policy.Decide(Request(IrreversibleSkill)),
            _policy.Decide(Request(IrreversibleSkill, autonomyLevel: AutonomyLevel.Assisted, allowIrreversibleUnattended: true)),
            _policy.Decide(Request(ReversibleSkill, autonomyLevel: AutonomyLevel.Assisted))
        };

        foreach (var refusal in refusals)
        {
            refusal.Allowed.ShouldBeFalse();
            refusal.Reason!.ToLowerInvariant().ShouldNotContain("was disabled");
            refusal.Reason!.ToLowerInvariant().ShouldNotContain("was paused");
            refusal.Reason!.ToLowerInvariant().ShouldNotContain("task was");
        }
    }

    [Test]
    public void Decide_IrreversibleSkill_OnAScheduledTaskWithTheOptIn_IsAllowed()
    {
        Known(IrreversibleSkill);

        var decision = _policy.Decide(Request(IrreversibleSkill, allowIrreversibleUnattended: true));

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldBeNull();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.None);
    }

    [Test]
    public void Decide_IrreversibleSkill_WithTheOptInButTooLowALevel_IsDenied()
    {
        Known(IrreversibleSkill);

        var decision = _policy.Decide(Request(
            IrreversibleSkill,
            autonomyLevel: AutonomyLevel.Assisted,
            allowIrreversibleUnattended: true));

        decision.Allowed.ShouldBeFalse();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.AutonomyLevelTooLow);
    }

    [Test]
    public void Decide_IrreversibleSkill_OnTheHeartbeat_IgnoresTheOptInAndDenies()
    {
        Known(IrreversibleSkill);

        var decision = _policy.Decide(Request(
            IrreversibleSkill,
            autonomyLevel: AutonomyLevel.FullyAutonomous,
            executionKind: UnattendedExecutionKind.ProactiveHeartbeat,
            allowIrreversibleUnattended: true));

        decision.Allowed.ShouldBeFalse();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.IrreversibleWithoutOptIn);
    }

    [Test]
    public void Decide_ReadOnlySkill_IsAllowedEvenAtTheLowestLevel()
    {
        Known(ReadOnlySkill, SkillCategory.Query);

        var decision = _policy.Decide(Request(ReadOnlySkill, autonomyLevel: AutonomyLevel.Propose));

        decision.Allowed.ShouldBeTrue();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.None);
    }

    [Test]
    public void Decide_ReversibleSkill_AtAssisted_IsDenied()
    {
        Known(ReversibleSkill);

        var decision = _policy.Decide(Request(ReversibleSkill, autonomyLevel: AutonomyLevel.Assisted));

        decision.Allowed.ShouldBeFalse();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.AutonomyLevelTooLow);
    }

    [Test]
    public void Decide_ReversibleSkill_AtAutonomous_IsAllowed()
    {
        Known(ReversibleSkill);

        var decision = _policy.Decide(Request(ReversibleSkill, autonomyLevel: AutonomyLevel.Autonomous));

        decision.Allowed.ShouldBeTrue();
    }

    [Test]
    public void Decide_ScenarioGatedSkill_AtPropose_IsDenied()
    {
        Known(ScenarioGatedSkill);

        var decision = _policy.Decide(Request(ScenarioGatedSkill, autonomyLevel: AutonomyLevel.Propose));

        decision.Allowed.ShouldBeFalse();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.AutonomyLevelTooLow);
    }

    // Etappe 5 depends on this end to end: create_container_template must clear the hardened matrix at
    // the DEFAULT autonomy level without any per-task opt-in, otherwise the autonomous remediation of an
    // empty container is gated behind a wall it can never pass. The real classifier is used here, so this
    // breaks the moment the skill loses its registered inverse.
    [Test]
    public void Decide_CreateContainerTemplate_RunsUnattendedAtTheDefaultLevelWithoutAnOptIn()
    {
        Known("create_container_template");

        var decision = _policy.Decide(Request(
            "create_container_template",
            autonomyLevel: AutonomyDefaults.DefaultLevel,
            allowIrreversibleUnattended: false));

        decision.Allowed.ShouldBeTrue();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.None);
    }

    [Test]
    public void Decide_DeleteContainerTemplate_NeverRunsUnattended_NotEvenWithTheOptIn()
    {
        Known("delete_container_template");

        var decision = _policy.Decide(Request(
            "delete_container_template",
            autonomyLevel: AutonomyLevel.FullyAutonomous,
            allowIrreversibleUnattended: true));

        decision.Allowed.ShouldBeFalse();
        decision.DenyReason.ShouldBe(UnattendedDenyReason.SensitiveSkill);
    }

    [Test]
    public void Decide_ScenarioGatedSkill_AtAssisted_IsAllowed()
    {
        Known(ScenarioGatedSkill);

        var decision = _policy.Decide(Request(ScenarioGatedSkill, autonomyLevel: AutonomyLevel.Assisted));

        decision.Allowed.ShouldBeTrue();
    }
}
