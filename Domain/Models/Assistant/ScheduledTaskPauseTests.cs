// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the pause invariant of ScheduledTask: IsPaused and PausedReason are only ever moved together
/// through Pause and ClearPause, so a stale reason can never stand next to IsPaused=false. Enforcing this
/// on the entity rather than on the two call sites is the point - a third call site added later inherits
/// the guarantee instead of having to remember it.
/// </summary>

namespace Klacks.UnitTest.Domain.Models.Assistant;

[TestFixture]
public class ScheduledTaskPauseTests
{
    private static ScheduledTask Task() => new()
    {
        Id = Guid.NewGuid(),
        Name = "weekly check",
        CronExpression = "0 8 * * 1",
        TimeZoneId = "Europe/Zurich"
    };

    [Test]
    public void ANewTask_IsNeitherPausedNorCarriesAReason()
    {
        var task = Task();

        task.IsPaused.ShouldBeFalse();
        task.PausedReason.ShouldBeNull();
    }

    [Test]
    public void Pause_SetsBothTheFlagAndTheReason()
    {
        var task = Task();

        task.Pause("irreversible skill without the opt-in");

        task.IsPaused.ShouldBeTrue();
        task.PausedReason.ShouldBe("irreversible skill without the opt-in");
    }

    [Test]
    public void ClearPause_DropsTheReasonTogetherWithTheFlag()
    {
        var task = Task();
        task.Pause("irreversible skill without the opt-in");

        task.ClearPause();

        task.IsPaused.ShouldBeFalse();
        task.PausedReason.ShouldBeNull();
    }

    [Test]
    public void Pause_LeavesTheOwnersEnabledIntentAndTheScheduleAlone()
    {
        var task = Task();
        task.IsEnabled = true;
        task.NextRunUtc = new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

        task.Pause("autonomy level too low");

        task.IsEnabled.ShouldBeTrue();
        task.NextRunUtc.ShouldBe(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void NeitherPauseFieldHasAPublicSetter_SoTheyCannotDriftApart()
    {
        var type = typeof(ScheduledTask);

        type.GetProperty(nameof(ScheduledTask.IsPaused))!.SetMethod!.IsPublic.ShouldBeFalse();
        type.GetProperty(nameof(ScheduledTask.PausedReason))!.SetMethod!.IsPublic.ShouldBeFalse();
    }
}
