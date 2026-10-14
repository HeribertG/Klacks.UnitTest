// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ContainerChildIds.HasSameEntriesAs: two sets are the same when they hold the same child Works and the
/// same child Breaks, whatever the order; a Work id and a Break id never stand in for each other.
/// </summary>

namespace Klacks.UnitTest.Domain.Models.Schedules;

[TestFixture]
public class ContainerChildIdsTests
{
    [Test]
    public void TheSameEntriesInAnotherOrder_AreTheSame()
    {
        var work1 = Guid.NewGuid();
        var work2 = Guid.NewGuid();
        var break1 = Guid.NewGuid();

        new ContainerChildIds([work1, work2], [break1])
            .HasSameEntriesAs(new ContainerChildIds([work2, work1], [break1])).ShouldBeTrue();
    }

    [Test]
    public void NoEntriesOnBothSides_AreTheSame()
    {
        ContainerChildIds.None.HasSameEntriesAs(new ContainerChildIds([], [])).ShouldBeTrue();
    }

    [Test]
    public void AnExtraChildWork_IsNotTheSame()
    {
        var work1 = Guid.NewGuid();

        new ContainerChildIds([work1], []).HasSameEntriesAs(new ContainerChildIds([work1, Guid.NewGuid()], []))
            .ShouldBeFalse();
    }

    [Test]
    public void AnExtraChildBreak_IsNotTheSame()
    {
        new ContainerChildIds([], []).HasSameEntriesAs(new ContainerChildIds([], [Guid.NewGuid()])).ShouldBeFalse();
    }

    [Test]
    public void ASwappedChildBreak_IsNotTheSame()
    {
        new ContainerChildIds([], [Guid.NewGuid()]).HasSameEntriesAs(new ContainerChildIds([], [Guid.NewGuid()]))
            .ShouldBeFalse();
    }

    [Test]
    public void TheSameIdAsWorkOnOneSideAndAsBreakOnTheOther_IsNotTheSame()
    {
        var id = Guid.NewGuid();

        new ContainerChildIds([id], []).HasSameEntriesAs(new ContainerChildIds([], [id])).ShouldBeFalse();
    }
}
