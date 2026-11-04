// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Which children move to the second part of a container split, and when a split is refused: only between two children,
/// never through one, inside the container, with a task on each side; containers crossing midnight included.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class ContainerSplitPartitionTests
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    private static ContainerSplitItem TaskItem(Guid id, TimeOnly start, TimeOnly end) => new(id, start, end, true);

    [Test]
    public void SplitBetweenTwoTasks_MovesTheLaterTaskAndTheAbsenceAfterIt()
    {
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        var absence = Guid.NewGuid();

        var after = ContainerSplitPartition.ItemsAfter(T(8), T(16), T(12),
            [TaskItem(early, T(8), T(11)), TaskItem(late, T(13), T(15)), new ContainerSplitItem(absence, T(15), T(15, 30), false)]);

        after.ShouldBe(new[] { late, absence }, ignoreOrder: true);
    }

    [Test]
    public void SplitThroughATask_IsRefused()
    {
        Should.Throw<InvalidRequestException>(() => ContainerSplitPartition.ItemsAfter(T(8), T(16), T(14),
            [TaskItem(Guid.NewGuid(), T(8), T(11)), TaskItem(Guid.NewGuid(), T(13), T(15))]));
    }

    [Test]
    public void SplitLeavingOnePartWithoutTask_IsRefused()
    {
        Should.Throw<InvalidRequestException>(() => ContainerSplitPartition.ItemsAfter(T(8), T(16), T(12),
            [TaskItem(Guid.NewGuid(), T(8), T(11)), new ContainerSplitItem(Guid.NewGuid(), T(13), T(14), false)]));
    }

    [TestCase(8)]
    [TestCase(16)]
    [TestCase(18)]
    public void SplitOnOrOutsideTheBounds_IsRefused(int hour)
    {
        Should.Throw<InvalidRequestException>(() => ContainerSplitPartition.ItemsAfter(T(8), T(16), T(hour),
            [TaskItem(Guid.NewGuid(), T(8), T(11)), TaskItem(Guid.NewGuid(), T(13), T(15))]));
    }

    [Test]
    public void NightContainer_SplitAfterMidnight_MovesTheMorningTask_AndTheSecondPartIsOnTheNextDay()
    {
        var morning = Guid.NewGuid();

        var after = ContainerSplitPartition.ItemsAfter(T(22), T(6), T(2),
            [TaskItem(Guid.NewGuid(), T(22, 30), T(1)), TaskItem(morning, T(3), T(5))]);

        after.ShouldBe(new[] { morning });
        ContainerSplitPartition.SecondPartDate(new DateOnly(2026, 8, 15), T(22), T(2)).ShouldBe(new DateOnly(2026, 8, 16));
        ContainerSplitPartition.SecondPartDate(new DateOnly(2026, 8, 15), T(8), T(12)).ShouldBe(new DateOnly(2026, 8, 15));
    }
}
