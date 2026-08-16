// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the EvaluateAutonomyLevelChange handler. Uses the REAL SkillRiskClassifier
/// against a small fixed registry (one skill per risk class, via names already known to
/// SkillRiskClassifier's fixed lists) so the assertions pin actual production behavior instead of
/// a re-implemented truth table. Covers the documented divergence between the chat gate
/// (AutonomyGateService.IsAllowed) and the plan-step gate (PlanStepApprovalPolicy.RequiresApproval)
/// for Irreversible at Autonomous vs. FullyAutonomous, and the Sensitive invariant (never changes).
/// </summary>

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class EvaluateAutonomyLevelChangeQueryHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // One skill per risk class, using real names already classified by SkillRiskClassifier's fixed
    // lists (place_work -> Reversible via InverseSkillRegistry, cover_absence -> ScenarioGated,
    // set_autonomy_level -> Sensitive) plus two fixture names for ReadOnly/Irreversible.
    private static readonly SkillDescriptor ReadOnlySkill = Descriptor("fixture_readonly_probe", SkillCategory.Query);
    private static readonly SkillDescriptor ReversibleSkill = Descriptor("place_work", SkillCategory.Crud);
    private static readonly SkillDescriptor ScenarioGatedSkill = Descriptor("cover_absence", SkillCategory.Crud);
    private static readonly SkillDescriptor IrreversibleSkill = Descriptor("fixture_irreversible_probe", SkillCategory.Crud);
    private static readonly SkillDescriptor SensitiveSkill = Descriptor("set_autonomy_level", SkillCategory.Crud);

    private IAgentAutonomyPreferenceRepository _preferenceRepository = null!;
    private ISkillRegistry _skillRegistry = null!;

    [SetUp]
    public void Setup()
    {
        _preferenceRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _skillRegistry.GetAllSkills().Returns(new List<SkillDescriptor>
        {
            ReadOnlySkill, ReversibleSkill, ScenarioGatedSkill, IrreversibleSkill, SensitiveSkill
        });
    }

    private static SkillDescriptor Descriptor(string name, SkillCategory category) => new(
        Name: name,
        Description: "fixture",
        Category: category,
        Parameters: Array.Empty<SkillParameter>(),
        RequiredPermissions: Array.Empty<string>(),
        RequiredCapabilities: Array.Empty<LLMCapability>(),
        ImplementationType: null);

    private EvaluateAutonomyLevelChangeQueryHandler Handler() =>
        new(_preferenceRepository, _skillRegistry, new SkillRiskClassifier());

    private void SetCurrentLevel(AutonomyLevel level)
    {
        _preferenceRepository.GetAsync(UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = UserId.ToString(), Level = level });
    }

    private static AutonomyRiskClassImpact Impact(IReadOnlyList<AutonomyRiskClassImpact> impacts, SkillRiskClass riskClass) =>
        impacts.Single(i => i.RiskClass == riskClass.ToString());

    [Test]
    public async Task NoOp_WhenTargetEqualsCurrent()
    {
        SetCurrentLevel(AutonomyLevel.Autonomous);

        var result = await Handler().Handle(
            new EvaluateAutonomyLevelChangeQuery(UserId, AutonomyLevel.Autonomous), CancellationToken.None);

        Assert.That(result.IsNoOp, Is.True);
        Assert.That(result.IsDowngrade, Is.False);
        Assert.That(result.SkillsNewlyUnconfirmedInChat, Is.EqualTo(0));
    }

    [Test]
    public async Task ProposeToAssisted_UnlocksReversibleAndScenarioGated_OnChatGate()
    {
        SetCurrentLevel(AutonomyLevel.Propose);

        var result = await Handler().Handle(
            new EvaluateAutonomyLevelChangeQuery(UserId, AutonomyLevel.Assisted), CancellationToken.None);

        Assert.That(result.SkillsNewlyUnconfirmedInChat, Is.EqualTo(2), "place_work + cover_absence");

        var reversible = Impact(result.Impacts, SkillRiskClass.Reversible);
        Assert.That(reversible.ChatConfirmationRequiredAtCurrent, Is.True);
        Assert.That(reversible.ChatConfirmationRequiredAtTarget, Is.False);

        var scenarioGated = Impact(result.Impacts, SkillRiskClass.ScenarioGated);
        Assert.That(scenarioGated.ChatConfirmationRequiredAtCurrent, Is.True);
        Assert.That(scenarioGated.ChatConfirmationRequiredAtTarget, Is.False);

        var irreversible = Impact(result.Impacts, SkillRiskClass.Irreversible);
        Assert.That(irreversible.ChatBehaviorChanges, Is.False, "still needs Autonomous+");
    }

    [Test]
    public async Task AssistedToAutonomous_UnlocksIrreversible_OnChatGate_ButNotOnPlanGate()
    {
        SetCurrentLevel(AutonomyLevel.Assisted);

        var result = await Handler().Handle(
            new EvaluateAutonomyLevelChangeQuery(UserId, AutonomyLevel.Autonomous), CancellationToken.None);

        Assert.That(result.SkillsNewlyUnconfirmedInChat, Is.EqualTo(1), "fixture_irreversible_probe");

        var irreversible = Impact(result.Impacts, SkillRiskClass.Irreversible);
        Assert.That(irreversible.ChatBehaviorChanges, Is.True);
        Assert.That(irreversible.PlanBehaviorChanges, Is.False, "plan gate needs FullyAutonomous, one step stricter");
    }

    [Test]
    public async Task AutonomousToFullyAutonomous_ChangesNothingOnChatGate_ButUnlocksIrreversibleOnPlanGate()
    {
        SetCurrentLevel(AutonomyLevel.Autonomous);

        var result = await Handler().Handle(
            new EvaluateAutonomyLevelChangeQuery(UserId, AutonomyLevel.FullyAutonomous), CancellationToken.None);

        Assert.That(result.SkillsNewlyUnconfirmedInChat, Is.EqualTo(0),
            "the chat gate already allows Irreversible from Autonomous upward");

        var irreversible = Impact(result.Impacts, SkillRiskClass.Irreversible);
        Assert.That(irreversible.ChatBehaviorChanges, Is.False);
        Assert.That(irreversible.PlanApprovalRequiredAtCurrent, Is.True);
        Assert.That(irreversible.PlanApprovalRequiredAtTarget, Is.False);
        Assert.That(irreversible.PlanBehaviorChanges, Is.True);
    }

    [Test]
    public async Task Downgrade_ReconfirmsUnlockedClasses()
    {
        SetCurrentLevel(AutonomyLevel.Autonomous);

        var result = await Handler().Handle(
            new EvaluateAutonomyLevelChangeQuery(UserId, AutonomyLevel.Propose), CancellationToken.None);

        Assert.That(result.IsDowngrade, Is.True);
        Assert.That(result.SkillsNewlyConfirmedInChat, Is.EqualTo(3),
            "place_work + cover_absence + fixture_irreversible_probe");
    }

    [Test]
    public async Task Sensitive_NeverChanges_OnEitherGate_AcrossFullRange()
    {
        SetCurrentLevel(AutonomyLevel.Propose);

        var result = await Handler().Handle(
            new EvaluateAutonomyLevelChangeQuery(UserId, AutonomyLevel.FullyAutonomous), CancellationToken.None);

        var sensitive = Impact(result.Impacts, SkillRiskClass.Sensitive);
        Assert.That(sensitive.ChatConfirmationRequiredAtCurrent, Is.True);
        Assert.That(sensitive.ChatConfirmationRequiredAtTarget, Is.True);
        Assert.That(sensitive.PlanApprovalRequiredAtCurrent, Is.True);
        Assert.That(sensitive.PlanApprovalRequiredAtTarget, Is.True);
        Assert.That(sensitive.ChatBehaviorChanges, Is.False);
        Assert.That(sensitive.PlanBehaviorChanges, Is.False);
    }

    [Test]
    public async Task MissingPreference_FallsBackToDefaultLevel()
    {
        _preferenceRepository.GetAsync(UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns((AgentAutonomyPreferenceRow?)null);

        var result = await Handler().Handle(
            new EvaluateAutonomyLevelChangeQuery(UserId, AutonomyLevel.FullyAutonomous), CancellationToken.None);

        Assert.That(result.CurrentLevel, Is.EqualTo((int)AutonomyDefaults.DefaultLevel));
    }
}
