// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
        new ConfirmationRedemptionGuard(turnScope),
        Array.Empty<ISkillConfirmationPreviewProvider>(),
        NullLogger<AutonomyGateService>.Instance);

    private static SkillDescriptor Descriptor(string name = "test_skill", SkillCategory category = SkillCategory.Crud)
        => new(name, "test", category, [], [], [], null);

    private static SkillExecutionContext Context(
        Guid? userId = null,
        bool bypass = false,
        UserConfirmationEvidence evidence = UserConfirmationEvidence.None) => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        BypassAutonomyGate = bypass,
        UserConfirmation = evidence
    };

    private static SkillExecutionContext YesContext() =>
        Context(evidence: UserConfirmationEvidence.ExplicitConfirmationReply);

    private static SkillExecutionContext McpContext(Guid? userId = null) =>
        Context(userId) with { ExternalAgentAccessMode = PersonalAccessTokenAccessMode.Write };

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
        new ConfirmationRedemptionGuard(_turnScope),
        Array.Empty<ISkillConfirmationPreviewProvider>(),
        NullLogger<AutonomyGateService>.Instance);

    [Test]
    public async Task Check_ReadOnlySkill_AlwaysAllowed_EvenAtProposeLevel()
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.ReadOnly);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    // Fill-empty-only writers (owner decision 2026-10-24: geocode_addresses stores coordinates without asking)
    // pass at every level, Propose included, and issue no token.
    [Test]
    public async Task Check_FillEmptyOnlySkill_AlwaysAllowed_EvenAtProposeLevel()
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.FillEmptyOnly);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        result.ShouldBeNull();
        _turnScope.IssuedTokens.ShouldBeEmpty();
    }

    [Test]
    public async Task Check_GeocodeAddresses_WithTheRealClassifier_RunsUnconfirmedAtProposeLevel()
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);

        var result = await CreateGateWithRealRiskClassifier().CheckAsync(
            Descriptor("geocode_addresses"), context, new Dictionary<string, object>());

        result.ShouldBeNull();
    }

    [TestCase(SkillRiskClass.Reversible)]
    [TestCase(SkillRiskClass.ScenarioGated)]
    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.Sensitive)]
    public async Task Check_ProposeLevel_HoldsAllWriters(SkillRiskClass riskClass)
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.Metadata, Does.ContainKey("confirmationToken"));
    }

    // Every token the gate issues is recorded on the turn's scope - a turn the user stops drops exactly
    // these - and, whatever the risk class, carries the same-turn redemption refusal.
    [TestCase(SkillRiskClass.Reversible)]
    [TestCase(SkillRiskClass.ScenarioGated)]
    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.Sensitive)]
    public async Task Check_AHeldSkill_RecordsItsTokenOnTheTurnScopeWhateverItsRiskClass(SkillRiskClass riskClass)
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        var token = (string)result!.Metadata!["confirmationToken"];
        _turnScope.IssuedTokens.ShouldBe([token]);
        _turnScope.WasIssuedThisTurn(token).ShouldBeTrue();
    }

    [Test]
    public async Task Check_AnAllowedSkill_RecordsNoToken()
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);
        SetRisk(SkillRiskClass.Reversible);

        await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        _turnScope.IssuedTokens.ShouldBeEmpty();
    }

    [TestCase(SkillRiskClass.Reversible, true)]
    [TestCase(SkillRiskClass.ScenarioGated, true)]
    [TestCase(SkillRiskClass.Irreversible, false)]
    [TestCase(SkillRiskClass.Sensitive, false)]
    public async Task Check_AssistedLevel_AllowsReversibleAndScenarioGatedOnly(SkillRiskClass riskClass, bool allowed)
    {
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
        _preferenceRepository.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentAutonomyPreferenceRow?)null);
        SetRisk(SkillRiskClass.Irreversible);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Check_ValidConfirmationToken_AllowsAndStripsParameter()
    {
        var context = YesContext();
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

    [TestCase(SkillRiskClass.Reversible)]
    [TestCase(SkillRiskClass.ScenarioGated)]
    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.Sensitive)]
    public async Task Check_TokenSameTurnRedemption_IsRefusedWithACleanError_NextTurnAllowed(SkillRiskClass riskClass)
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(riskClass);
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

        sameTurn.ShouldNotBeNull();
        sameTurn.Type.ShouldBe(SkillResultType.Error);
        sameTurn.Message.ShouldBe(ConfirmationRedemptionMessages.IssuedThisTurn);
        nextTurn.ShouldBeNull();
    }

    [Test]
    public async Task Check_TokenWithoutUserConfirmationEvidence_IsRefusedWithACleanError_AndStaysRedeemable()
    {
        var userId = Guid.NewGuid();
        SetLevel(userId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var descriptor = Descriptor("delete_shift");
        var token = _confirmationStore.Create(userId, "delete_shift", new Dictionary<string, object>());

        var withoutEvidence = await _sut.CheckAsync(
            descriptor,
            Context(userId, evidence: UserConfirmationEvidence.None),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });
        var withEvidence = await _sut.CheckAsync(
            descriptor,
            Context(userId, evidence: UserConfirmationEvidence.ExplicitConfirmationReply),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        withoutEvidence.ShouldNotBeNull();
        withoutEvidence.Type.ShouldBe(SkillResultType.Error);
        withoutEvidence.Message.ShouldBe(ConfirmationRedemptionMessages.UserConfirmationMissing);
        withEvidence.ShouldBeNull();
    }

    [Test]
    public async Task Check_AHeldCallOverMcp_CarriesNeitherTheTokenNorARedemptionInstruction_AndTheTokenIsBoundToMcp()
    {
        var context = McpContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);

        var held = await _sut.CheckAsync(Descriptor("delete_shift"), context, new Dictionary<string, object>());
        var token = (string)held!.Metadata!["confirmationToken"];

        held.Type.ShouldBe(SkillResultType.Confirmation);
        held.Message.ShouldNotContain(token);
        held.Message.ShouldNotContain(AutonomyDefaults.ConfirmPendingActionSkillName);
        held.Message.ShouldContain(ConfirmationRedemptionMessages.HeldOverMcp);
        _confirmationStore.PeekLatestForUser(context.UserId, TimeSpan.FromMinutes(1)).ShouldBeNull();
        _confirmationStore.PeekLatestForUser(
                context.UserId, TimeSpan.FromMinutes(1), channel: PendingConfirmationChannels.Mcp)
            .ShouldNotBeNull();
    }

    [Test]
    public async Task Check_ATokenHeldOverMcp_IsNotRedeemableByTheChat_EvenWithAnExplicitYes()
    {
        var userId = Guid.NewGuid();
        SetLevel(userId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var descriptor = Descriptor("delete_shift");
        var held = await _sut.CheckAsync(descriptor, McpContext(userId), new Dictionary<string, object>());
        var token = (string)held!.Metadata!["confirmationToken"];

        var chatGate = CreateGate(new TurnConfirmationScope());
        var viaChat = await chatGate.CheckAsync(
            descriptor,
            Context(userId, evidence: UserConfirmationEvidence.ExplicitConfirmationReply),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        viaChat.ShouldNotBeNull();
        viaChat.Type.ShouldBe(SkillResultType.Confirmation);
        ((string)viaChat.Metadata!["confirmationToken"]).ShouldNotBe(token);
    }

    [Test]
    public async Task Check_RefusedToken_WhenTheLevelAllowsTheSkillAnyway_StillRuns()
    {
        var userId = Guid.NewGuid();
        SetLevel(userId, AutonomyLevel.Autonomous);
        SetRisk(SkillRiskClass.Irreversible);
        var token = _confirmationStore.Create(userId, "delete_shift", new Dictionary<string, object>());

        var result = await _sut.CheckAsync(
            Descriptor("delete_shift"),
            Context(userId, evidence: UserConfirmationEvidence.None),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        result.ShouldBeNull();
    }

    [Test]
    public async Task Check_ConfirmationMessage_TellsTheModelToAskAndStop_NotToConfirmAtOnce()
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);

        var result = await _sut.CheckAsync(Descriptor("delete_shift"), context, new Dictionary<string, object>());

        result!.Message.ShouldContain("Ask the user to explicitly confirm");
        result.Message.ShouldContain("stop");
        result.Message.ShouldContain("NEXT turn");
        result.Message.ShouldNotContain("immediately call");
        result.Message.ShouldNotContain("do NOT ask again");
    }

    [Test]
    public async Task Check_TokenWithDifferentParameters_HeldAgain()
    {
        var context = YesContext();
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
    public async Task Check_ClosePeriodTokenIssuedWithoutAcknowledgement_DoesNotCoverTheSealAnywayCall()
    {
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor("close_period");
        var plainSeal = new Dictionary<string, object> { ["groupName"] = "Group A" };
        var token = _confirmationStore.Create(context.UserId, "close_period", plainSeal);

        var result = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            ["groupName"] = "Group A",
            ["acknowledgeViolations"] = true,
            ["acknowledgedErrorCount"] = 3,
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        result.ShouldNotBeNull();
        result.Type.ShouldBe(SkillResultType.Confirmation);
        ((string)result.Metadata!["confirmationToken"]).ShouldNotBe(token);
    }

    [Test]
    public async Task Check_TokenWithDifferentParameters_IsBurned_SameActionHeldAfterwards()
    {
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();

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
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
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
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.FullyAutonomous);

        var result = await gate.CheckAsync(Descriptor(skillName), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
    }

    [Test]
    public async Task Check_CreateCalendarSelection_RealClassifier_AllowedAtAutonomousLevel()
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = YesContext();
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

        var result = await gate.CheckAsync(Descriptor(skillName), YesContext(), new Dictionary<string, object>());

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

        var result = await gate.CheckAsync(Descriptor(skillName), YesContext(), new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    // Owner decision 2026-10-12 ("only deletes and resets ask"): through the REAL classifier, at the Autonomous
    // default level, every destructive delete/remove/reset skill and every secret writer is held for confirmation.
    [TestCase("delete_shift")]
    [TestCase("delete_address")]
    [TestCase("delete_qualification")]
    [TestCase("delete_communication")]
    [TestCase("delete_membership")]
    [TestCase("remove_client_from_group")]
    [TestCase("reset_shift_cuts")]
    [TestCase("reset_container_day")]
    [TestCase("delete_expense")]
    [TestCase("update_deepl_settings")]
    [TestCase("update_web_search_settings")]
    [TestCase("update_openroute_settings")]
    [TestCase("update_speech_settings")]
    [TestCase("update_email_settings")]
    [TestCase("update_imap_settings")]
    [TestCase("create_llm_provider")]
    [TestCase("update_llm_model")]
    public async Task Check_DestructiveAndSecretWriterSkills_RealClassifier_HeldAtDefaultLevel(string skillName)
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);

        var result = await gate.CheckAsync(Descriptor(skillName), context, new Dictionary<string, object>());

        result.ShouldNotBeNull(skillName);
        result.Type.ShouldBe(SkillResultType.Confirmation, skillName);
    }

    // The exceptions stay as they were: a delete with a registered inverse (delete_work, delete_break) is
    // Reversible, and assistant-internal bookkeeping stays Irreversible; none of them asks at the default level.
    [TestCase("delete_work")]
    [TestCase("delete_break")]
    [TestCase("delete_ai_memory")]
    [TestCase("delete_agent_skill")]
    [TestCase("delete_transcription_dictionary_entry")]
    public async Task Check_DeleteSkillsWithoutDataLoss_RealClassifier_NotHeldAtDefaultLevel(string skillName)
    {
        var gate = CreateGateWithRealRiskClassifier();
        var context = YesContext();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);

        var result = await gate.CheckAsync(Descriptor(skillName), context, new Dictionary<string, object>());

        result.ShouldBeNull(skillName);
    }
}
