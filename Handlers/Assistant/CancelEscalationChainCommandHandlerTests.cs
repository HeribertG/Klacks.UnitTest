// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for CancelEscalationChainCommandHandler: the visibility gate (a hidden chain answers like a resolved one and
/// never reaches the service), the cancel gate (admin, supervisor or holder of an open stage of this chain may cancel,
/// any other planner gets Forbidden) and the pass-through onto IEscalationChainService.CancelAsync.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant.Escalation;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class CancelEscalationChainCommandHandlerTests
{
    private const string UserId = "planner-a";
    private const string UserName = "Planner A";
    private const string Reason = "Shift already covered by another employee.";

    private IEscalationChainService _chainService = null!;
    private IEscalationChainRepository _chainRepository = null!;
    private IEscalationChainVisibilityFilter _visibilityFilter = null!;
    private IUserService _userService = null!;
    private CancelEscalationChainCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _chainService = Substitute.For<IEscalationChainService>();
        _chainRepository = Substitute.For<IEscalationChainRepository>();
        _visibilityFilter = Substitute.For<IEscalationChainVisibilityFilter>();
        _userService = Substitute.For<IUserService>();
        _userService.IsAdmin().Returns(false);
        _userService.GetRights().Returns(new List<string>());
        _visibilityFilter.IsVisibleAsync(Arg.Any<EscalationChain>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _sut = new CancelEscalationChainCommandHandler(_chainService, _chainRepository, _visibilityFilter, _userService);
    }

    private EscalationChain GivenChain(Guid chainId, params EscalationStage[] stages)
    {
        var chain = new EscalationChain { Id = chainId, GroupId = Guid.NewGuid(), Stages = stages.ToList() };
        _chainRepository.GetByIdWithStagesAsync(chainId, Arg.Any<CancellationToken>()).Returns(chain);
        return chain;
    }

    [Test]
    public async Task Handle_SupervisorCancelsVisibleChain_ForwardsToServiceAndReportsCancelled()
    {
        var chainId = Guid.NewGuid();
        GivenChain(chainId);
        _userService.GetRights().Returns(new List<string> { Roles.Authorised });
        _chainService.CancelAsync(chainId, UserId, UserName, Reason, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, UserId, UserName, Reason), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationCancelOutcome.Cancelled));
        Assert.That(result.Result.Success, Is.True);
        await _chainService.Received(1).CancelAsync(chainId, UserId, UserName, Reason, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AdminCancelsChain_IsAllowedWithoutOwnStage()
    {
        var chainId = Guid.NewGuid();
        GivenChain(chainId);
        _userService.IsAdmin().Returns(true);
        _chainService.CancelAsync(chainId, UserId, UserName, Reason, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, UserId, UserName, Reason), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationCancelOutcome.Cancelled));
    }

    [TestCase(EscalationStageStatus.Pending)]
    [TestCase(EscalationStageStatus.Notified)]
    public async Task Handle_PlannerHoldingAnOpenStageOfThisChain_MayCancel(EscalationStageStatus status)
    {
        var chainId = Guid.NewGuid();
        GivenChain(chainId, new EscalationStage { UserId = UserId, Status = status });
        _chainService.CancelAsync(chainId, UserId, UserName, Reason, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, UserId, UserName, Reason), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationCancelOutcome.Cancelled));
    }

    [Test]
    public async Task Handle_PlannerWithoutStageOnThisChain_IsForbiddenAndServiceNotCalled()
    {
        var chainId = Guid.NewGuid();
        GivenChain(chainId, new EscalationStage { UserId = "someone-else", Status = EscalationStageStatus.Notified });

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, UserId, UserName, Reason), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationCancelOutcome.Forbidden));
        Assert.That(result.Result.Success, Is.False);
        await _chainService.DidNotReceive().CancelAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_PlannerWhoseStageAlreadyAnsweredOrExpired_IsForbidden()
    {
        var chainId = Guid.NewGuid();
        GivenChain(chainId, new EscalationStage { UserId = UserId, Status = EscalationStageStatus.Expired });

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, UserId, UserName, Reason), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationCancelOutcome.Forbidden));
    }

    [Test]
    public async Task Handle_ChainHiddenFromCaller_AnswersLikeResolvedAndServiceNotCalled()
    {
        var chainId = Guid.NewGuid();
        GivenChain(chainId, new EscalationStage { UserId = UserId, Status = EscalationStageStatus.Notified });
        _visibilityFilter.IsVisibleAsync(Arg.Any<EscalationChain>(), UserId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, UserId, UserName, Reason), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationCancelOutcome.AlreadyResolved));
        Assert.That(result.Result.Success, Is.False);
        await _chainService.DidNotReceive().CancelAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_MissingChain_ReturnsAlreadyResolved()
    {
        var chainId = Guid.NewGuid();
        _chainService.CancelAsync(chainId, UserId, UserName, Reason, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, UserId, UserName, Reason), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationCancelOutcome.AlreadyResolved));
        Assert.That(result.Result.Messages, Is.Not.Empty);
    }
}
