// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduledTaskDuePolicy: the fire / skip-stale / not-due decision around the
/// catch-up window.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Scheduling;

namespace Klacks.UnitTest.Application.Assistant.Scheduling;

[TestFixture]
public class ScheduledTaskDuePolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ScheduledTaskDuePolicy _policy = new();

    [Test]
    public void Decide_NotDue_WhenNextRunIsNull()
    {
        _policy.Decide(null, Now, Window).ShouldBe(ScheduledTaskRunDecision.NotDue);
    }

    [Test]
    public void Decide_NotDue_WhenNextRunIsInFuture()
    {
        _policy.Decide(Now.AddMinutes(1), Now, Window).ShouldBe(ScheduledTaskRunDecision.NotDue);
    }

    [Test]
    public void Decide_Fire_WhenExactlyDue()
    {
        _policy.Decide(Now, Now, Window).ShouldBe(ScheduledTaskRunDecision.Fire);
    }

    [Test]
    public void Decide_Fire_WhenDueWithinWindow()
    {
        _policy.Decide(Now.AddMinutes(-5), Now, Window).ShouldBe(ScheduledTaskRunDecision.Fire);
    }

    [Test]
    public void Decide_Fire_OnWindowBoundary()
    {
        _policy.Decide(Now.AddMinutes(-15), Now, Window).ShouldBe(ScheduledTaskRunDecision.Fire);
    }

    [Test]
    public void Decide_SkipStale_WhenBeyondWindow()
    {
        _policy.Decide(Now.AddHours(-1), Now, Window).ShouldBe(ScheduledTaskRunDecision.SkipStale);
    }
}
