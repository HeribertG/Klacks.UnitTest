// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConfirmPendingActionSkillTests
{
    private IPendingConfirmationStore _store = null!;
    private ISkillExecutor _skillExecutor = null!;
    private TurnConfirmationScope _turnScope = null!;
    private ConfirmPendingActionSkill _sut = null!;

    [SetUp]
    public void Setup()
    {
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _turnScope = new TurnConfirmationScope();
        _sut = new ConfirmPendingActionSkill(_store, _skillExecutor, new ConfirmationRedemptionGuard(_turnScope));
    }

    private static SkillExecutionContext Ctx(
        Guid? userId = null,
        UserConfirmationEvidence evidence = UserConfirmationEvidence.None) => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        UserConfirmation = evidence
    };

    private static SkillExecutionContext YesCtx(Guid? userId = null) =>
        Ctx(userId, UserConfirmationEvidence.ExplicitConfirmationReply);

    [Test]
    public async Task ValidToken_ReplaysStoredInvocationWithBypass()
    {
        var context = YesCtx();
        var storedParams = new Dictionary<string, object> { ["level"] = "0" };
        var token = _store.Create(context.UserId, "set_autonomy_level", storedParams);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }, "done"));

        var result = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result.Success, Is.True);
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i =>
                i.SkillName == "set_autonomy_level" &&
                i.Parameters["level"].ToString() == "0"),
            Arg.Is<SkillExecutionContext>(c => c.BypassAutonomyGate),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARowWithABinding_ReplaysWithTheBindingOnTheContext_AndNotInTheParameters()
    {
        var context = YesCtx();
        var binding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, new[] { "w1", "w2" });
        var token = _store.Create(
            context.UserId, "bulk_delete_works", new Dictionary<string, object> { ["fromDate"] = "2026-11-01" },
            PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat, binding);
        SkillExecutionContext? seen = null;
        SkillInvocation? invocation = null;
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                invocation = call.Arg<SkillInvocation>();
                seen = call.Arg<SkillExecutionContext>();
                return SkillResult.SuccessResult(null);
            });

        await _sut.ExecuteAsync(context, new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        seen.ShouldNotBeNull();
        seen.ConfirmedActionBinding.ShouldBe(binding);
        seen.BypassAutonomyGate.ShouldBeTrue();
        invocation!.Parameters.Keys.ShouldBe(new[] { "fromDate" });
    }

    [Test]
    public async Task ARowWithABindingOfAMacroSwitch_CarriesItToo()
    {
        var context = YesCtx();
        var binding = ConfirmationBinding.Of(ConfirmationBindingKinds.MacroSwitch, new[] { "Shift|a|-|b" });
        var token = _store.Create(
            context.UserId, "assign_macro_to_shift", new Dictionary<string, object>(),
            PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat, binding);
        SkillExecutionContext? seen = null;
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.Arg<SkillExecutionContext>();
                return SkillResult.SuccessResult(null);
            });

        await _sut.ExecuteAsync(context, new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        seen!.ConfirmedActionBinding.ShouldBe(binding);
    }

    [Test]
    public async Task ARowWithoutABinding_ReplaysWithNoneOnTheContext_EvenWhenTheCallerContextCarriedOne()
    {
        var context = YesCtx() with { ConfirmedActionBinding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, new[] { "x" }) };
        var token = _store.Create(context.UserId, "delete_shift", new Dictionary<string, object>());
        SkillExecutionContext? seen = null;
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.Arg<SkillExecutionContext>();
                return SkillResult.SuccessResult(null);
            });

        await _sut.ExecuteAsync(context, new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        seen!.ConfirmedActionBinding.ShouldBeNull();
    }

    [Test]
    public async Task InvalidToken_ReturnsErrorWithoutExecution()
    {
        var result = await _sut.ExecuteAsync(YesCtx(), new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = "no-such-token"
        });

        Assert.That(result.Success, Is.False);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TokenOfOtherUser_ReturnsErrorWithoutExecution()
    {
        var token = _store.Create(Guid.NewGuid(), "delete_shift", new Dictionary<string, object>());

        var result = await _sut.ExecuteAsync(YesCtx(), new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result.Success, Is.False);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Token_IsSingleUse()
    {
        var context = YesCtx();
        var token = _store.Create(context.UserId, "delete_shift", new Dictionary<string, object>());
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null));

        var first = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var second = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(first.Success, Is.True);
        Assert.That(second.Success, Is.False);
    }

    [TestCase("close_period")]
    [TestCase("delete_shift")]
    [TestCase("place_work")]
    public async Task TokenFromSameTurn_WhateverTheSkillsRiskClass_ReturnsErrorAndKeepsTokenValid(string skillName)
    {
        var context = YesCtx();
        var token = _store.Create(context.UserId, skillName, new Dictionary<string, object>());
        _turnScope.MarkIssued(token);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null));

        var sameTurn = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var nextTurnSkill = new ConfirmPendingActionSkill(
            _store, _skillExecutor, new ConfirmationRedemptionGuard(new TurnConfirmationScope()));
        var nextTurn = await nextTurnSkill.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        sameTurn.Success.ShouldBeFalse();
        sameTurn.Message.ShouldBe(ConfirmationRedemptionMessages.IssuedThisTurn);
        nextTurn.Success.ShouldBeTrue();
    }

    [Test]
    public async Task LaterTurnWithoutUserConfirmationEvidence_ReturnsErrorAndKeepsTokenValid()
    {
        var userId = Guid.NewGuid();
        var token = _store.Create(userId, "delete_shift", new Dictionary<string, object>());
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null));

        var withoutEvidence = await _sut.ExecuteAsync(
            Ctx(userId, UserConfirmationEvidence.None),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        withoutEvidence.Success.ShouldBeFalse();
        withoutEvidence.Message.ShouldBe(ConfirmationRedemptionMessages.UserConfirmationMissing);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());

        var withEvidence = await _sut.ExecuteAsync(
            Ctx(userId, UserConfirmationEvidence.ExplicitConfirmationReply),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        withEvidence.Success.ShouldBeTrue();
    }

    [Test]
    public async Task AClientEndpointCallWithoutTheChatsEvidence_CannotRedeemAnEarlierToken()
    {
        var userId = Guid.NewGuid();
        var token = _store.Create(userId, "delete_shift", new Dictionary<string, object>());
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null));

        var result = await _sut.ExecuteAsync(
            Ctx(userId),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ConfirmationRedemptionMessages.UserConfirmationMissing);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        _store.PeekLatestForUser(userId, TimeSpan.FromMinutes(1)).ShouldNotBeNull();
    }

    [Test]
    public async Task ATokenIssuedOverMcp_IsNotRedeemableInTheChat_AndIsBurnedByTheAttempt()
    {
        var userId = Guid.NewGuid();
        var token = _store.Create(
            userId, "delete_shift", new Dictionary<string, object>(),
            PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Mcp);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null));

        var result = await _sut.ExecuteAsync(
            YesCtx(userId),
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        result.Success.ShouldBeFalse();
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        _store.Consume(token, userId, channel: PendingConfirmationChannels.Mcp).ShouldBeNull();
    }

    [Test]
    public async Task ContextWithBypassedGate_IsRefusedBeforeTheTokenIsConsumed()
    {
        var context = YesCtx();
        var token = _store.Create(context.UserId, "close_period", new Dictionary<string, object>());
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null));

        var background = await _sut.ExecuteAsync(context with { BypassAutonomyGate = true }, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(background.Success, Is.False);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());

        var interactive = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(interactive.Success, Is.True);
    }

    [Test]
    public async Task MissingToken_ReturnsError()
    {
        var result = await _sut.ExecuteAsync(YesCtx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidToken_CarriesTheReplayedSkillNameInTheResultMetadata()
    {
        var context = YesCtx();
        var token = _store.Create(context.UserId, "update_contract", new Dictionary<string, object>());
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }, "done"));

        var result = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Metadata, Is.Not.Null);
        Assert.That(result.Metadata![SkillResultMetadataKeys.ReplayedSkillName], Is.EqualTo("update_contract"));
    }

    [Test]
    public async Task ValidToken_KeepsTheReplayedResultsOwnMetadata()
    {
        var context = YesCtx();
        var token = _store.Create(context.UserId, "update_contract", new Dictionary<string, object>());
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Confirmation("Please confirm.", "inner-token"));

        var result = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result.Metadata![SkillResultMetadataKeys.ConfirmationToken], Is.EqualTo("inner-token"));
        Assert.That(result.Metadata[SkillResultMetadataKeys.ReplayedSkillName], Is.EqualTo("update_contract"));
    }

    [Test]
    public async Task ARedeemedCorrectionUndo_RunsTheInverse_ButCarriesNoReplayedSkillName()
    {
        var context = YesCtx();
        var token = _store.Create(
            context.UserId, "remove_shift_from_group", new Dictionary<string, object>(),
            PendingConfirmationPurposes.CorrectionUndo);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }, "undone"));

        var result = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result.Success, Is.True);
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "remove_shift_from_group"),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());
        Assert.That(result.Metadata?.ContainsKey(SkillResultMetadataKeys.ReplayedSkillName) ?? false, Is.False);
    }

    [TestCase("correctionundo", false)]
    [TestCase("CORRECTIONUNDO", false)]
    [TestCase("   ", true)]
    [TestCase("GateReplay", true)]
    public async Task ThePurposeOfTheRedeemedToken_IsComparedNormalized(string purpose, bool carriesReplayedName)
    {
        var context = YesCtx();
        var token = _store.Create(context.UserId, "update_contract", new Dictionary<string, object>(), purpose);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }, "done"));

        var result = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result.Metadata?.ContainsKey(SkillResultMetadataKeys.ReplayedSkillName) ?? false, Is.EqualTo(carriesReplayedName));
    }

    [Test]
    public async Task InvalidToken_CarriesNoReplayedSkillName()
    {
        var result = await _sut.ExecuteAsync(YesCtx(), new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = "no-such-token"
        });

        Assert.That(result.Metadata?.ContainsKey(SkillResultMetadataKeys.ReplayedSkillName) ?? false, Is.False);
    }
}
