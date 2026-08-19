// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ThoroughRecalculationOnMonthlyTargetHoursChangedHandler: a changed month queues a
/// thorough recalculation spanning exactly that calendar month without any client scope, and a
/// queue failure is swallowed defensively.
/// </summary>

using Klacks.Api.Domain.Events;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class ThoroughRecalculationOnMonthlyTargetHoursChangedHandlerTests
{
    private IThoroughRecalculationQueue _queue = null!;
    private ThoroughRecalculationOnMonthlyTargetHoursChangedHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _queue = Substitute.For<IThoroughRecalculationQueue>();
        _sut = new ThoroughRecalculationOnMonthlyTargetHoursChangedHandler(
            _queue, NullLogger<ThoroughRecalculationOnMonthlyTargetHoursChangedHandler>.Instance);
    }

    [Test]
    public async Task Handle_ChangedMonth_QueuesExactMonthWindowWithoutClientScope()
    {
        await _sut.HandleAsync(new MonthlyTargetHoursChangedEvent(2026, 2));

        _queue.Received(1).QueueRecalculation(
            new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), null, null);
    }

    [Test]
    public async Task Handle_December_QueuesUntilDecemberThirtyFirst()
    {
        await _sut.HandleAsync(new MonthlyTargetHoursChangedEvent(2026, 12));

        _queue.Received(1).QueueRecalculation(
            new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), null, null);
    }

    [Test]
    public async Task Handle_QueueFailure_IsSwallowedDefensively()
    {
        _queue.QueueRecalculation(default, default, null, null)
            .ReturnsForAnyArgs(_ => throw new InvalidOperationException("boom"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(new MonthlyTargetHoursChangedEvent(2026, 2)));
    }
}
