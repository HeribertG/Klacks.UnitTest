// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The last move of a work goes back from the schedule through the Work reassignment after the shared check; a hidden work
/// reads like a missing one.
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
    private IMoveReversalCheck _check = null!;
    private IMediator _mediator = null!;
    private ReverseMovedWorkCommandHandler _handler = null!;
    private Work _work = null!;

    [SetUp]
    public void SetUp()
    {
        _work = new Work { Id = _workId, ClientId = _owner, MovedFromClientId = _origin };
        _works = Substitute.For<IWorkRepository>();
        _works.GetNoTracking(_workId).Returns(_ => _work);
        _access = Substitute.For<IScheduleRowAccessGuard>();
        _access.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _check = Substitute.For<IMoveReversalCheck>();
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>()).Returns(new ReassignWorkClientResponse());
        _handler = new ReverseMovedWorkCommandHandler(_works, _access, _check, _mediator);
    }

    private Task<ReassignWorkClientResponse?> Reverse() => _handler.Handle(new ReverseMovedWorkCommand(_workId), CancellationToken.None);

    [Test]
    public async Task AMovedWork_IsCheckedAndGoesBackToItsOrigin()
    {
        await Reverse();

        Received.InOrder(() =>
        {
            _check.EnsureReversibleAsync(_work, Arg.Any<CancellationToken>());
            _mediator.Send(Arg.Is<ReassignWorkClientCommand>(c => c.Id == _workId && c.TargetClientId == _origin), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task AHiddenWork_ReadsLikeAMissingOne()
    {
        _access.IsVisibleAsync(_owner, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(Reverse);
        await _check.DidNotReceiveWithAnyArgs().EnsureReversibleAsync(default!, default);
    }

    [Test]
    public async Task ARefusalOfTheCheck_WritesNothing()
    {
        _check.EnsureReversibleAsync(_work, Arg.Any<CancellationToken>())
            .Returns(_ => throw new MoveReversalRefusedException("busy", MoveReversalRefusalCodes.OriginBusy));

        await Should.ThrowAsync<MoveReversalRefusedException>(Reverse);
        await _mediator.DidNotReceive().Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>());
    }
}
