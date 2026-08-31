// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pure schedule tests for ProactiveReminderSchedule (package F "repeat until acknowledged"): the
/// backoff ladder 1h/4h/24h/48h and the repetition of the last step once the schedule is exhausted.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ProactiveReminderScheduleTests
{
    private static readonly DateTime Now = new(2026, 8, 31, 8, 0, 0, DateTimeKind.Utc);

    [Test]
    public void FirstDueAfter_IsOneHourLater()
    {
        var due = ProactiveReminderSchedule.FirstDueAfter(Now);

        Assert.That(due, Is.EqualTo(Now.AddHours(1)));
    }

    [Test]
    public void NextDueAfter_ZeroRemindersSent_RepeatsTheFirstStep()
    {
        var due = ProactiveReminderSchedule.NextDueAfter(remindersSent: 0, Now);

        Assert.That(due, Is.EqualTo(Now.AddHours(1)));
    }

    [Test]
    public void NextDueAfter_OneReminderSent_IsFourHoursLater()
    {
        var due = ProactiveReminderSchedule.NextDueAfter(remindersSent: 1, Now);

        Assert.That(due, Is.EqualTo(Now.AddHours(4)));
    }

    [Test]
    public void NextDueAfter_TwoRemindersSent_IsTwentyFourHoursLater()
    {
        var due = ProactiveReminderSchedule.NextDueAfter(remindersSent: 2, Now);

        Assert.That(due, Is.EqualTo(Now.AddHours(24)));
    }

    [Test]
    public void NextDueAfter_ThreeRemindersSent_IsFortyEightHoursLater()
    {
        var due = ProactiveReminderSchedule.NextDueAfter(remindersSent: 3, Now);

        Assert.That(due, Is.EqualTo(Now.AddHours(48)));
    }

    [Test]
    public void NextDueAfter_FourRemindersSent_RepeatsTheLastStep()
    {
        var due = ProactiveReminderSchedule.NextDueAfter(remindersSent: 4, Now);

        Assert.That(due, Is.EqualTo(Now.AddHours(48)));
    }

    [Test]
    public void NextDueAfter_FarBeyondTheSchedule_StillRepeatsTheLastStep()
    {
        var due = ProactiveReminderSchedule.NextDueAfter(remindersSent: 42, Now);

        Assert.That(due, Is.EqualTo(Now.AddHours(48)));
    }
}
