// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// BreakLockRule: a Closed break is written by an admin only, a sealed break is deleted by an admin only, and a break of a
/// scenario is never locked, like a Work under ParentWorkLockGuard.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class BreakLockRuleTests
{
    [Test]
    public void EnsureWritable_RefusesAClosedBreak_ForANonAdmin()
    {
        var ex = Should.Throw<InvalidRequestException>(
            () => BreakLockRule.EnsureWritable(NewBreak(WorkLockLevel.Closed, null), false));

        ex.Message.ShouldBe(BreakLockRule.ClosedBreakMessage);
    }

    [TestCase(WorkLockLevel.None)]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    public void EnsureWritable_LetsAnyBreakBelowClosedThrough(WorkLockLevel level)
    {
        Should.NotThrow(() => BreakLockRule.EnsureWritable(NewBreak(level, null), false));
    }

    [Test]
    public void EnsureWritable_LetsAnAdminWriteAClosedBreak()
    {
        Should.NotThrow(() => BreakLockRule.EnsureWritable(NewBreak(WorkLockLevel.Closed, null), true));
    }

    [Test]
    public void EnsureWritable_DoesNotLockABreakOfAScenario()
    {
        Should.NotThrow(() => BreakLockRule.EnsureWritable(NewBreak(WorkLockLevel.Closed, Guid.NewGuid()), false));
    }

    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public void EnsureDeletable_RefusesEverySealedBreak_ForANonAdmin(WorkLockLevel level)
    {
        var ex = Should.Throw<InvalidRequestException>(
            () => BreakLockRule.EnsureDeletable(NewBreak(level, null), false));

        ex.Message.ShouldBe(BreakLockRule.SealedBreakDeleteMessage);
    }

    [Test]
    public void EnsureDeletable_LetsANonAdminDeleteAnUnsealedBreak()
    {
        Should.NotThrow(() => BreakLockRule.EnsureDeletable(NewBreak(WorkLockLevel.None, null), false));
    }

    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Closed)]
    public void EnsureDeletable_LetsAnAdminDeleteASealedBreak(WorkLockLevel level)
    {
        Should.NotThrow(() => BreakLockRule.EnsureDeletable(NewBreak(level, null), true));
    }

    [Test]
    public void EnsureDeletable_DoesNotLockABreakOfAScenario()
    {
        Should.NotThrow(() => BreakLockRule.EnsureDeletable(NewBreak(WorkLockLevel.Approved, Guid.NewGuid()), false));
    }

    [Test]
    public void EnsureDeletable_WithoutAStoredBreak_RefusesNothing()
    {
        Should.NotThrow(() => BreakLockRule.EnsureDeletable(null, false));
    }

    private static Break NewBreak(WorkLockLevel level, Guid? analyseToken)
        => new() { Id = Guid.NewGuid(), LockLevel = level, AnalyseToken = analyseToken };
}
