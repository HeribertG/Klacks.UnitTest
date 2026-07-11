// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConfirmPendingActionSkillTests
{
    private InMemoryPendingConfirmationStore _store = null!;
    private ISkillExecutor _skillExecutor = null!;
    private TurnConfirmationScope _turnScope = null!;
    private ConfirmPendingActionSkill _sut = null!;

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryPendingConfirmationStore();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _turnScope = new TurnConfirmationScope();
        _sut = new ConfirmPendingActionSkill(_store, _skillExecutor, _turnScope);
    }

    private static SkillExecutionContext Ctx(Guid? userId = null) => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    [Test]
    public async Task ValidToken_ReplaysStoredInvocationWithBypass()
    {
        var context = Ctx();
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
                (string)i.Parameters["level"] == "0"),
            Arg.Is<SkillExecutionContext>(c => c.BypassAutonomyGate),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidToken_ReturnsErrorWithoutExecution()
    {
        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>
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

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>
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
        var context = Ctx();
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

    [Test]
    public async Task SensitiveTokenFromSameTurn_ReturnsErrorAndKeepsTokenValid()
    {
        var context = Ctx();
        var token = _store.Create(context.UserId, "close_period", new Dictionary<string, object>());
        _turnScope.MarkIssuedForSensitiveSkill(token);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null));

        var sameTurn = await _sut.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var nextTurnSkill = new ConfirmPendingActionSkill(_store, _skillExecutor, new TurnConfirmationScope());
        var nextTurn = await nextTurnSkill.ExecuteAsync(context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(sameTurn.Success, Is.False);
        Assert.That(nextTurn.Success, Is.True);
    }

    [Test]
    public async Task MissingToken_ReturnsError()
    {
        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }
}
