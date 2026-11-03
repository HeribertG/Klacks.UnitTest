// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The last move of a work goes back from the schedule through the Work reassignment; a work already back or held is
/// refused with its code, a hidden one reads like a missing one.
/// </summary>

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ReverseMovedWorkCommandHandlerTests
{
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _origin = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    private IWorkRepository _works = null!;
    private IScheduleRowAccessGuard _access = null!;
    private ICoverMoveFactsReader _facts = null!;
    private IMediator _mediator = null!;
    private ReverseMovedWorkCommandHandler _handler = null!;
    private Work _work = null!;
    private CoverMoveFacts _fact = null!;

    [SetUp]
    public void SetUp()
    {
        _work = new Work { Id = _workId, ClientId = _owner, MovedFromClientId = _origin };
        _fact = new CoverMoveFacts(
            _workId, _owner, null, new DateOnly(2026, 11, 4), new TimeOnly(8, 0), new TimeOnly(16, 0), WorkLockLevel.None, false, false, true);
        _works = Substitute.For<IWorkRepository>();
        _works.GetNoTracking(_workId).Returns(_ => _work);
        _access = Substitute.For<IScheduleRowAccessGuard>();
        _access.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _facts = Substitute.For<ICoverMoveFactsReader>();
        _facts.LoadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, CoverMoveFacts>)new Dictionary<Guid, CoverMoveFacts> { [_workId] = _fact });
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>()).Returns(new ReassignWorkClientResponse());
        _handler = new ReverseMovedWorkCommandHandler(_works, _access, _facts, _mediator);
    }

    private Task<ReassignWorkClientResponse?> Reverse() => _handler.Handle(new ReverseMovedWorkCommand(_workId), CancellationToken.None);

    [Test]
    public async Task AMovedWork_GoesBackToItsOrigin()
    {
        await Reverse();

        await _mediator.Received(1).Send(
            Arg.Is<ReassignWorkClientCommand>(c => c.Id == _workId && c.TargetClientId == _origin), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AHiddenWork_ReadsLikeAMissingOne()
    {
        _access.IsVisibleAsync(_owner, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(Reverse);
    }

    [Test]
    public async Task AWorkWithoutOrigin_IsRefusedAsBack()
    {
        _work.MovedFromClientId = null;

        var refused = await Should.ThrowAsync<MoveReversalRefusedException>(Reverse);

        refused.ErrorCode.ShouldBe(MoveReversalRefusalCodes.AlreadyBack);
        await _mediator.DidNotReceive().Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AHeldWork_IsRefusedAsHeld()
    {
        _fact = _fact with { HasReplacement = true };

        var refused = await Should.ThrowAsync<MoveReversalRefusedException>(Reverse);

        refused.ErrorCode.ShouldBe(MoveReversalRefusalCodes.Held);
    }
}
