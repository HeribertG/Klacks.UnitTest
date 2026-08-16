// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AutonomyGateServiceTests
{
    private IAgentAutonomyPreferenceRepository _preferenceRepository = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private TurnConfirmationScope _turnScope = null!;
    private AutonomyGateService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _preferenceRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _confirmationStore = PendingStoreTestFactory.CreateConfirmationStore();
        _turnScope = new TurnConfirmationScope();
        _sut = CreateGate(_turnScope);
    }

    private AutonomyGateService CreateGate(TurnConfirmationScope turnScope) => new(
        _preferenceRepository,
        _riskClassifier,
        _confirmationStore,
        turnScope,
        NullLogger<AutonomyGateService>.Instance);

    private static SkillDescriptor Descriptor(string name = "test_skill", SkillCategory category = SkillCategory.Crud)
        => new(name, "test", category, [], [], [], null);

    private static SkillExecutionContext Context(Guid? userId = null, bool bypass = false) => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        BypassAutonomyGate = bypass
    };

    private void SetLevel(Guid userId, AutonomyLevel level)
    {
        _preferenceRepository.GetAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = userId.ToString(), Level = level });
    }

    private void SetRisk(SkillRiskClass riskClass)
    {
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(riskClass);
    }

    // Uses the REAL SkillRiskClassifier (not the mocked _riskClassifier) so Classify() actually
    // runs end-to-end: this is the durable regression guard that a future refactor of the gate or
    // the risk-class enum cannot silently un-gate update/delete_calendar_selection (payroll-relevant,
    // owner decision) or accidentally gate create_calendar_selection (deliberately autonomous).
    private AutonomyGateService CreateGateWithRealRiskClassifier() => new(
        _preferenceRepository,
        new SkillRiskClassifier(),
        _confirmationStore,
        _turnScope,
        NullLogger<AutonomyGateService>.Instance);

    [Test]
    public async Task Check_ReadOnlySkill_AlwaysAllowed_EvenAtProposeLevel()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.ReadOnly);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    [TestCase(SkillRiskClass.Reversible)]
    [TestCase(SkillRiskClass.ScenarioGated)]
    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.Sensitive)]
    public async Task Check_ProposeLevel_HoldsAllWriters(SkillRiskClass riskClass)
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.Metadata, Does.ContainKey("confirmationToken"));
    }

    [TestCase(SkillRiskClass.Reversible, true)]
    [TestCase(SkillRiskClass.ScenarioGated, true)]
    [TestCase(SkillRiskClass.Irreversible, false)]
    [TestCase(SkillRiskClass.Sensitive, false)]
    public async Task Check_AssistedLevel_AllowsReversibleAndScenarioGatedOnly(SkillRiskClass riskClass, bool allowed)
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Assisted);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result == null, Is.EqualTo(allowed));
    }

    [TestCase(SkillRiskClass.Reversible, true)]
    [TestCase(SkillRiskClass.ScenarioGated, true)]
    [TestCase(SkillRiskClass.Irreversible, true)]
    public async Task Check_AutonomousLevel_AllowsEveryRiskClassExceptSensitive(SkillRiskClass riskClass, bool allowed)
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result == null, Is.EqualTo(allowed));
    }

    // Regression guard for the matrix at the top level: raising the level to FullyAutonomous must not
    // change anything for the non-sensitive classes, so a future edit cannot loosen them unnoticed.
    [TestCase(SkillRiskClass.ReadOnly, true)]
    [TestCase(SkillRiskClass.Reversible, true)]
    [TestCase(SkillRiskClass.ScenarioGated, true)]
    [TestCase(SkillRiskClass.Irreversible, true)]
    public async Task Check_FullyAutonomousLevel_AllowsEveryRiskClassExceptSensitive(SkillRiskClass riskClass, bool allowed)
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.FullyAutonomous);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result == null, Is.EqualTo(allowed));
    }

    // The Ui promises in the autonomy dialog that sensitive actions (user management, permissions,
    // autonomy changes) ALWAYS need a confirmation. This is that promise: every level, including
    // FullyAutonomous, and including the Autonomous default the dialog shows.
    [TestCase(AutonomyLevel.Propose)]
    [TestCase(AutonomyLevel.Assisted)]
    [TestCase(AutonomyLevel.Autonomous)]
    [TestCase(AutonomyLevel.FullyAutonomous)]
    public async Task Check_SensitiveSkill_HeldAtEveryAutonomyLevel(AutonomyLevel level)
    {
        var context = Context();
        SetLevel(context.UserId, level);
        SetRisk(SkillRiskClass.Sensitive);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.Metadata, Does.ContainKey("confirmationToken"));
    }

    [Test]
    public async Task Check_NoPreferenceRow_UsesDefaultLevel_AllowsIrreversible()
    {
        var context = Context();
        _preferenceRepository.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentAutonomyPreferenceRow?)null);
        SetRisk(SkillRiskClass.Irreversible);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Check_ValidConfirmationToken_AllowsAndStripsParameter()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var descriptor = Descriptor("delete_shift");
        var token = _confirmationStore.Create(
            context.UserId, "delete_shift", new Dictionary<string, object> { ["id"] = "x" });
        var parameters = new Dictionary<string, object>
        {
            ["id"] = "x",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        };

        var result = await _sut.CheckAsync(descriptor, context, parameters);

        Assert.That(result, Is.Null);
        Assert.That(parameters, Does.Not.ContainKey(AutonomyDefaults.ConfirmationTokenParameter));
    }

    [Test]
    public async Task Check_SensitiveToken_SameTurnRedemption_HeldAgain_NextTurnAllowed()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor("close_period");
        var parameters = new Dictionary<string, object> { ["groupName"] = "Group A" };

        var hold = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>(parameters));
        var token = (string)hold!.Metadata!["confirmationToken"];

        var sameTurn = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>(parameters)
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var nextTurnGate = CreateGate(new TurnConfirmationScope());
        var nextTurn = await nextTurnGate.CheckAsync(descriptor, context, new Dictionary<string, object>(parameters)
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(sameTurn, Is.Not.Null);
        Assert.That(nextTurn, Is.Null);
    }

    [Test]
    public async Task Check_TokenWithDifferentParameters_HeldAgain()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor("close_period");
        var token = _confirmationStore.Create(
            context.UserId, "close_period", new Dictionary<string, object> { ["groupName"] = "Group A" });

        var result = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            ["groupName"] = "Group B",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result, Is.Not.Null);
        Assert.That((string)result!.Metadata!["confirmationToken"], Is.Not.EqualTo(token));
    }

    [Test]
    public async Task Check_TokenWithDifferentParameters_IsBurned_SameActionHeldAfterwards()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor("close_period");
        var storedParameters = new Dictionary<string, object> { ["groupName"] = "Group A" };
        var token = _confirmationStore.Create(context.UserId, "close_period", storedParameters);

        await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            ["groupName"] = "Group B",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var replayWithOriginalParameters = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            ["groupName"] = "Group A",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(replayWithOriginalParameters, Is.Not.Null);
    }

    [Test]
    public async Task Check_TokenIsSingleUse_SecondCallHeldAgain()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var descriptor = Descriptor("delete_shift");
        var token = _confirmationStore.Create(context.UserId, "delete_shift", new Dictionary<string, object>());

        var first = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var second = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(first, Is.Null);
        Assert.That(second, Is.Not.Null);
    }

    [Test]
    public async Task Check_TokenForOtherSkill_Held()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var token = _confirmationStore.Create(context.UserId, "delete_shift", new Dictionary<string, object>());

        var result = await _sut.CheckAsync(Descriptor("delete_group"), context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Check_TokenForOtherUser_Held()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var token = _confirmationStore.Create(Guid.NewGuid(), "delete_shift", new Dictionary<string, object>());

        var result = await _sut.CheckAsync(Descriptor("delete_shift"), context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Check_ConfirmationMessage_ContainsReplayableToken()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);

        var result = await _sut.CheckAsync(Descriptor("delete_shift"), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        var token = (string)result!.Metadata!["confirmationToken"];
        Assert.That(result.Message, Does.Contain(token));
        Assert.That(result.Message, Does.Contain(AutonomyDefaults.ConfirmationTokenParameter));
    }

    [Test]
    public async Task Check_BypassContext_SkipsGateEntirely()
    {
        var context = Context(bypass: true);
        SetRisk(SkillRiskClass.Sensitive);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
        await _preferenceRepository.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // End-to-end regression guard (real SkillRiskClassifier, no mock): proves the full Classifier -> Gate
    // chain for the calendar-selection skills specifically. These feed payroll/surcharge calculation, so
    // they must be held at the Autonomous default too, not only at a lowered level. A future classifier
    // change that moved them out of the sensitive list would break both cases below.
    [TestCase("update_calendar_selection")]
    [TestCase("delete_calendar_selection")]
    public async Task Check_CalendarSelectionSensitiveSkills_RealClassifier_HeldAtDefaultAndLoweredLevel(string skillName)
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = Context();

        SetLevel(context.UserId, AutonomyLevel.Autonomous);
        var atDefault = await gate.CheckAsync(Descriptor(skillName), context, new Dictionary<string, object>());

        SetLevel(context.UserId, AutonomyLevel.Assisted);
        var lowered = await gate.CheckAsync(Descriptor(skillName), context, new Dictionary<string, object>());

        Assert.That(atDefault, Is.Not.Null);
        Assert.That(atDefault!.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(lowered, Is.Not.Null);
        Assert.That(lowered!.Type, Is.EqualTo(SkillResultType.Confirmation));
    }

    // create_group is the target two advisory skills (evaluate_location_group_candidates,
    // evaluate_grouping_by_qualification) funnel toward; without Sensitive, their "creating a group
    // stays a separate, manual step" description would be a promise the classifier does not keep. A
    // future classifier change that moved it out of the sensitive list would break this case.
    [Test]
    public async Task Check_CreateGroupSensitive_RealClassifier_HeldAtDefaultLevel()
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);

        var result = await gate.CheckAsync(Descriptor("create_group"), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
    }

    // The three examples the Ui names in the autonomy dialog footer, through the REAL classifier, at the
    // Autonomous default the dialog shows as selected. This is the exact promise the dialog makes.
    [TestCase("delete_system_user")]
    [TestCase("assign_user_permissions")]
    [TestCase("set_autonomy_level")]
    public async Task Check_UiNamedSensitiveExamples_RealClassifier_HeldAtDefaultLevel(string skillName)
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);

        var result = await gate.CheckAsync(Descriptor(skillName), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
    }

    // The owner split MCP-hiding from the Sensitive class: list_personal_access_tokens reads the
    // caller's OWN token metadata only (the plaintext is never persisted), so it no longer sits in
    // SensitiveSkills and must run through the chat gate un-gated at EVERY level, the lowest included.
    // Its invisibility to external agents is now carried by McpSkillExposurePolicy.ExcludedSkills -
    // see McpSkillExposurePolicyTests.
    [TestCase(AutonomyLevel.Propose)]
    [TestCase(AutonomyLevel.Assisted)]
    [TestCase(AutonomyLevel.Autonomous)]
    [TestCase(AutonomyLevel.FullyAutonomous)]
    public async Task Check_ListPersonalAccessTokens_RealClassifier_NeverConfirmed(AutonomyLevel level)
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = Context();
        SetLevel(context.UserId, level);

        var result = await gate.CheckAsync(
            Descriptor("list_personal_access_tokens", SkillCategory.Query),
            context,
            new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    // The counterpart: the two PAT MUTATIONS stay Sensitive and therefore stay confirmed at every
    // level, including FullyAutonomous. Splitting the read out must not have loosened them.
    [TestCase("create_personal_access_token")]
    [TestCase("revoke_personal_access_token")]
    public async Task Check_PatMutationSkills_RealClassifier_HeldAtEveryLevel(string skillName)
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.FullyAutonomous);

        var result = await gate.CheckAsync(Descriptor(skillName), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
    }

    [Test]
    public async Task Check_CreateCalendarSelection_RealClassifier_AllowedAtAutonomousLevel()
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);

        var result = await gate.CheckAsync(
            Descriptor("create_calendar_selection"), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    // Counterpart to the removal of apply_grouping from the sensitive list (owner decision): at the
    // Autonomous default level the apply must run straight through, because the preview it belongs to
    // was already confirmed by the user. No SetLevel here on purpose — the repository returns null, so
    // the gate falls back to AutonomyDefaults.DefaultLevel, i.e. a user who never configured a level.
    [TestCase("apply_grouping")]
    public async Task Check_GroupingApplySkill_RealClassifier_NotHeld_AtDefaultAutonomyLevel(string skillName)
    {
        var gate = CreateGateWithRealRiskClassifier();

        var result = await gate.CheckAsync(Descriptor(skillName), Context(), new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    // Counterpart: the bulk group writers that default to apply=false must stay un-gated, because the
    // gate classifies by skill name only and would otherwise hold their read-only preview call too.
    [TestCase("fill_group_by_criteria")]
    [TestCase("bulk_add_shifts_to_group")]
    [TestCase("bulk_add_absence_for_group")]
    [TestCase("add_selected_clients_to_group")]
    [TestCase("group_ungrouped_by_city_name")]
    public async Task Check_PreviewDefaultBulkGroupWriters_RealClassifier_NotHeldAtDefaultLevel(string skillName)
    {
        var gate = CreateGateWithRealRiskClassifier();

        var result = await gate.CheckAsync(Descriptor(skillName), Context(), new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }
}
