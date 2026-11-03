// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Only the last applied move goes back, through the Work reassignment; every other row is refused with its reason.
/// </summary>

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ReverseMoveCommandHandlerTests
{
    private readonly Guid _requestId = Guid.NewGuid();
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _absent = Guid.NewGuid();
    private readonly Guid _standIn = Guid.NewGuid();

    private IReplacementRequestRepository _repository = null!;
    private IClientVisibilityGuard _visibility = null!;
    private IScenarioTokenAccessGuard _scenarios = null!;
    private IWorkRepository _works = null!;
    private ICoverMoveFactsReader _facts = null!;
    private IMediator _mediator = null!;
    private ReverseMoveCommandHandler _handler = null!;
    private ReplacementRequest _row = null!;
    private Work _work = null!;
    private CoverMoveFacts _fact = null!;

    [SetUp]
    public void SetUp()
    {
        _row = new ReplacementRequest
        {
            Id = _requestId,
            AbsentClientId = _absent,
            CandidateClientId = _standIn,
            Kind = ReplacementRequestKind.Move,
            WorkId = _workId,
            AppliedAtUtc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
        };
        _work = new Work { Id = _workId, ClientId = _standIn, MovedFromClientId = _absent };
        _fact = new CoverMoveFacts(
            _workId, _standIn, null, new DateOnly(2026, 11, 2), new TimeOnly(8, 0), new TimeOnly(16, 0), WorkLockLevel.None, false, false, true);
        _repository = Substitute.For<IReplacementRequestRepository>();
        _repository.Get(_requestId).Returns(_ => _row);
        _visibility = Substitute.For<IClientVisibilityGuard>();
        _visibility.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        _scenarios = Substitute.For<IScenarioTokenAccessGuard>();
        _scenarios.IsVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _works = Substitute.For<IWorkRepository>();
        _works.GetNoTracking(_workId).Returns(_ => _work);
        _facts = Substitute.For<ICoverMoveFactsReader>();
        _facts.LoadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, CoverMoveFacts>)new Dictionary<Guid, CoverMoveFacts> { [_workId] = _fact });
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>()).Returns(new ReassignWorkClientResponse());
        _handler = new ReverseMoveCommandHandler(_repository, _visibility, _scenarios, _works, _facts, _mediator);
    }

    private Task<ReassignWorkClientResponse?> Reverse() => _handler.Handle(new ReverseMoveCommand(_requestId), CancellationToken.None);

    private async Task AssertRefused(string reason)
    {
        var refused = await Should.ThrowAsync<MoveReversalRefusedException>(Reverse);

        refused.Message.ShouldContain(reason);
        refused.ErrorCode.ShouldStartWith("MOVE_");
        await _mediator.DidNotReceive().Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheLastMove_GoesBackToItsOrigin()
    {
        await Reverse();

        await _mediator.Received(1).Send(
            Arg.Is<ReassignWorkClientCommand>(c => c.Id == _workId && c.TargetClientId == _absent), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARowOfAnInvisiblePerson_ReadsLikeAMissingOne()
    {
        _visibility.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(Reverse);
    }

    [Test]
    public async Task AnOpenRowOfAHiddenScenario_ReadsLikeAMissingOne()
    {
        _row.AppliedAtUtc = null;
        _row.AnalyseToken = Guid.NewGuid();
        _scenarios.IsVisibleAsync(_row.AnalyseToken, Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(Reverse);
    }

    [Test]
    public async Task AnAppliedRow_DoesNotAskForItsAcceptedScenario()
    {
        _row.AnalyseToken = Guid.NewGuid();
        _scenarios.IsVisibleAsync(_row.AnalyseToken, Arg.Any<CancellationToken>()).Returns(false);

        await Reverse();

        await _mediator.Received(1).Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public Task AWorkAlreadyBack_IsRefusedAsBack()
    {
        _work.ClientId = _absent;
        _work.MovedFromClientId = null;
        return AssertRefused("already back");
    }

    [Test]
    public Task AReplacement_IsRefused()
    {
        _row.Kind = ReplacementRequestKind.Replacement;
        return AssertRefused("reached the plan");
    }

    [Test]
    public Task AMoveThatStayedInTheScenario_IsRefused()
    {
        _row.AppliedAtUtc = null;
        return AssertRefused("reached the plan");
    }

    [Test]
    public Task AReversedMove_IsRefused()
    {
        _row.ReversedAtUtc = DateTime.UtcNow;
        return AssertRefused("already taken back");
    }

    [Test]
    public Task AWorkThatMovedOn_IsRefused()
    {
        _work.ClientId = Guid.NewGuid();
        return AssertRefused("moved on");
    }

    [Test]
    public Task AWorkMovedAgainByItsOwner_IsRefused()
    {
        _work.MovedFromClientId = Guid.NewGuid();
        return AssertRefused("moved on");
    }

    [Test]
    public Task AnEarlierStepAfterALaterReversal_IsRefused()
    {
        _work.MovedFromClientId = null;
        return AssertRefused("moved on");
    }

    [Test]
    public Task AHeldWork_IsRefused()
    {
        _fact = _fact with { LockLevel = WorkLockLevel.Confirmed };
        return AssertRefused("sealed or replaced");
    }
}
