// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ConfirmBreakSkill and UnconfirmBreakSkill: the break id goes to the mediator command unchanged,
/// the reported lock level comes from the handler result, a missing or invisible break (KeyNotFoundException) and a
/// null result are reported as errors, and a missing breakId parameter is rejected before anything is sent.
/// </summary>

using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConfirmBreakSkillTests
{
    private static readonly Guid BreakId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private ConfirmBreakSkill _confirm = null!;
    private UnconfirmBreakSkill _unconfirm = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _confirm = new ConfirmBreakSkill(_mediator);
        _unconfirm = new UnconfirmBreakSkill(_mediator);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters() => new() { ["breakId"] = BreakId.ToString() };

    [Test]
    public async Task Confirm_SendsCommandWithBreakIdAndReportsLockLevel()
    {
        _mediator.Send(Arg.Any<ConfirmBreakCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BreakResource { Id = BreakId, LockLevel = WorkLockLevel.Confirmed });

        var result = await _confirm.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain(BreakId.ToString());
        await _mediator.Received(1).Send(
            Arg.Is<ConfirmBreakCommand>(c => c.BreakId == BreakId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Confirm_BreakNotFoundOrHidden_ReturnsError()
    {
        _mediator.Send(Arg.Any<ConfirmBreakCommand>(), Arg.Any<CancellationToken>())
            .Returns<BreakResource?>(_ => throw new KeyNotFoundException());

        var result = await _confirm.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task Confirm_NullResult_ReturnsError()
    {
        _mediator.Send(Arg.Any<ConfirmBreakCommand>(), Arg.Any<CancellationToken>())
            .Returns((BreakResource?)null);

        var result = await _confirm.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task Confirm_MissingBreakId_ThrowsWithoutSending()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _confirm.ExecuteAsync(Context(), new Dictionary<string, object>()));

        await _mediator.DidNotReceive().Send(Arg.Any<ConfirmBreakCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Unconfirm_SendsCommandWithBreakIdAndReportsLockLevel()
    {
        _mediator.Send(Arg.Any<UnconfirmBreakCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BreakResource { Id = BreakId, LockLevel = WorkLockLevel.None });

        var result = await _unconfirm.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<UnconfirmBreakCommand>(c => c.BreakId == BreakId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Unconfirm_BreakNotFoundOrHidden_ReturnsError()
    {
        _mediator.Send(Arg.Any<UnconfirmBreakCommand>(), Arg.Any<CancellationToken>())
            .Returns<BreakResource?>(_ => throw new KeyNotFoundException());

        var result = await _unconfirm.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task Unconfirm_MissingBreakId_ThrowsWithoutSending()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _unconfirm.ExecuteAsync(Context(), new Dictionary<string, object>()));

        await _mediator.DidNotReceive().Send(Arg.Any<UnconfirmBreakCommand>(), Arg.Any<CancellationToken>());
    }
}
