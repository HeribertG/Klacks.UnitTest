// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End to end across two turns with the REAL gate, the REAL confirm skill, the REAL risk classifier and the real
/// pending store: turn one holds an action, the model may not redeem the token in that same turn (any risk class),
/// and turn two redeems it only when the user's own message is an explicit yes - decided by the same detector the
/// chat loop uses to set the evidence. A refused redemption is a clean skill error that leaves the token valid.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConfirmationRedemptionPipelineTests
{
    private IAgentAutonomyPreferenceRepository _preferences = null!;
    private IPendingConfirmationStore _store = null!;
    private ISkillExecutor _skillExecutor = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _preferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "executed"));
    }

    private static SkillDescriptor Descriptor(string name) => new(name, "test", SkillCategory.Crud, [], [], [], null);

    private SkillExecutionContext Context(string userMessage) => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        UserConfirmation = AffirmationDetector.IsExplicitConfirmation(userMessage)
            ? UserConfirmationEvidence.ExplicitConfirmationReply
            : UserConfirmationEvidence.None
    };

    private (AutonomyGateService Gate, ConfirmPendingActionSkill Confirm) NewTurn()
    {
        var scope = new TurnConfirmationScope();
        var guard = new ConfirmationRedemptionGuard(scope);
        var gate = new AutonomyGateService(
            _preferences, new SkillRiskClassifier(), _store, scope, guard,
            Array.Empty<ISkillConfirmationPreviewProvider>(), NullLogger<AutonomyGateService>.Instance);
        return (gate, new ConfirmPendingActionSkill(_store, _skillExecutor, guard));
    }

    private static Dictionary<string, object> Redeem(string token) =>
        new() { [AutonomyDefaults.ConfirmationTokenParameter] = token };

    private async Task<string> HoldInTurnOneAsync(AutonomyGateService gate, string skillName, AutonomyLevel level)
    {
        _preferences.GetAsync(_userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = _userId.ToString(), Level = level });

        var held = await gate.CheckAsync(
            Descriptor(skillName), Context("Lösche das bitte"), new Dictionary<string, object> { ["id"] = "1" });

        held.ShouldNotBeNull();
        held.Type.ShouldBe(SkillResultType.Confirmation);
        return (string)held.Metadata!["confirmationToken"];
    }

    [TestCase("delete_shift", AutonomyLevel.Autonomous)]
    [TestCase("close_period", AutonomyLevel.Autonomous)]
    [TestCase("place_work", AutonomyLevel.Propose)]
    [TestCase("update_client", AutonomyLevel.Propose)]
    public async Task TheTokenOfAHeldAction_CannotBeRedeemedInTheTurnThatIssuedIt_WhateverTheRiskClass(
        string skillName, AutonomyLevel level)
    {
        var turnOne = NewTurn();
        var token = await HoldInTurnOneAsync(turnOne.Gate, skillName, level);

        var confirm = await turnOne.Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldBe(ConfirmationRedemptionMessages.IssuedThisTurn);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InTheNextTurn_AnExplicitYesRedeemsTheToken_AndTheHeldActionRunsExactlyOnce()
    {
        var token = await HoldInTurnOneAsync(NewTurn().Gate, "delete_shift", AutonomyLevel.Autonomous);

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue();
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(invocation => invocation.SkillName == "delete_shift"),
            Arg.Is<SkillExecutionContext>(context => context.BypassAutonomyGate),
            Arg.Any<CancellationToken>());

        var again = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        again.Success.ShouldBeFalse();
    }

    [TestCase("Zeig mir die Dienste bitte")]
    [TestCase("nein")]
    [TestCase("ja, aber nicht heute")]
    [TestCase("Lösche auch die Qualifikation")]
    public async Task InTheNextTurn_AMessageThatIsNotAYes_DoesNotRedeemTheToken_ButLeavesItValid(string userMessage)
    {
        var token = await HoldInTurnOneAsync(NewTurn().Gate, "delete_shift", AutonomyLevel.Autonomous);

        var refused = await NewTurn().Confirm.ExecuteAsync(Context(userMessage), Redeem(token));

        refused.Success.ShouldBeFalse();
        refused.Message.ShouldBe(ConfirmationRedemptionMessages.UserConfirmationMissing);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());

        var later = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        later.Success.ShouldBeTrue();
    }

    [Test]
    public async Task TheDirectTokenPath_FollowsTheSameRules_AsTheConfirmSkill()
    {
        var token = await HoldInTurnOneAsync(NewTurn().Gate, "delete_shift", AutonomyLevel.Autonomous);
        var parameters = () => new Dictionary<string, object>
        {
            ["id"] = "1",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        };

        var withoutYes = await NewTurn().Gate.CheckAsync(Descriptor("delete_shift"), Context("Lösche auch Y"), parameters());
        var withYes = await NewTurn().Gate.CheckAsync(Descriptor("delete_shift"), Context("Ja"), parameters());

        withoutYes.ShouldNotBeNull();
        withoutYes.Type.ShouldBe(SkillResultType.Error);
        withoutYes.Message.ShouldBe(ConfirmationRedemptionMessages.UserConfirmationMissing);
        withYes.ShouldBeNull();
    }
}
