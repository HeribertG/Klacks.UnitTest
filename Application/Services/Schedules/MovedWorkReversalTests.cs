// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Going back to the origin clears the marker and stamps the book; any other target changes nothing.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class MovedWorkReversalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _origin = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    private IReplacementRequestRepository _repository = null!;
    private MovedWorkReversal _reversal = null!;
    private Work _work = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IReplacementRequestRepository>();
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(Now);
        _reversal = new MovedWorkReversal(_repository, clock);
        _work = new Work { Id = _workId, ClientId = _owner, MovedFromClientId = _origin };
    }

    [Test]
    public async Task GoingBackToTheOrigin_ClearsTheMarker_AndStampsTheRow()
    {
        var row = new ReplacementRequest();
        _repository.FindAppliedMoveAsync(_workId, _origin, _owner, Arg.Any<CancellationToken>()).Returns(row);

        await _reversal.StageAsync(_work, _origin, CancellationToken.None);

        _work.MovedFromClientId.ShouldBeNull();
        row.ReversedAtUtc.ShouldBe(Now.UtcDateTime);
    }

    [Test]
    public async Task AWorkThatOnlyHungBack_ClearsTheMarker_WithoutARow()
    {
        await _reversal.StageAsync(_work, _origin, CancellationToken.None);

        _work.MovedFromClientId.ShouldBeNull();
    }

    [Test]
    public async Task AnotherTarget_ChangesNothing()
    {
        await _reversal.StageAsync(_work, Guid.NewGuid(), CancellationToken.None);

        _work.MovedFromClientId.ShouldBe(_origin);
        await _repository.DidNotReceiveWithAnyArgs().FindAppliedMoveAsync(default, default, default, default);
    }

    [Test]
    public async Task AnUnmovedWork_ChangesNothing()
    {
        _work.MovedFromClientId = null;

        await _reversal.StageAsync(_work, _origin, CancellationToken.None);

        await _repository.DidNotReceiveWithAnyArgs().FindAppliedMoveAsync(default, default, default, default);
    }
}
