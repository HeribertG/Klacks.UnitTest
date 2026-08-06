// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Recovery used to classify shifts with its own start-hour heuristic while both wizards asked
/// ShiftTypeInference, and the two disagreed. The category is not decorative - it feeds
/// RecoveryWork.Category and with it the PerformsShiftWork gate, so a disagreement means recovery
/// offers a replacement the planner would have refused. This walks a full day of realistic spans and
/// demands that recovery answers exactly what the shared inference answers.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderCategoryParityTests
{
    private const int ShiftLengthHours = 8;

    private static readonly Guid ClientId = new("00000000-0000-0000-0000-0000000000b1");
    private static readonly DateOnly Day = new(2026, 6, 3);

    [Test]
    public void BuildWorks_ClassifiesEveryStartHour_LikeTheWizards()
    {
        for (var hour = 0; hour < 24; hour++)
        {
            var works = RecoverySnapshotBuilder.BuildWorks([Cell(hour)], out _);

            var category = works[new CellKey(ClientId, Day)].Single().Category;

            category.ShouldBe(Expected(hour), $"start hour {hour}");
        }
    }

    [Test]
    public void BuildWorks_ClassicEarlyShift_IsEarly()
    {
        Category("06:00", "14:00").ShouldBe(ShiftCategory.Early);
    }

    [Test]
    public void BuildWorks_ShiftReachingIntoTheLateBand_IsLate()
    {
        // Starts in the early band, ends past 15:00 - the later band wins, exactly as in the wizards.
        Category("08:00", "16:00").ShouldBe(ShiftCategory.Late);
    }

    [Test]
    public void BuildWorks_ShiftStartingBeforeSix_IsNight()
    {
        // The old heuristic called this early, which quietly opened it to non-shift workers.
        Category("05:00", "13:00").ShouldBe(ShiftCategory.Night);
    }

    [Test]
    public void BuildWorks_ShiftReachingIntoTheNightBand_IsNight()
    {
        Category("15:00", "23:30").ShouldBe(ShiftCategory.Night);
    }

    [Test]
    public void BuildWorks_ClassicNightShift_IsNight()
    {
        Category("23:00", "07:00").ShouldBe(ShiftCategory.Night);
    }

    private static ShiftCategory Category(string start, string end)
    {
        var works = RecoverySnapshotBuilder.BuildWorks([Cell(TimeSpan.Parse(start), TimeSpan.Parse(end))], out _);
        return works[new CellKey(ClientId, Day)].Single().Category;
    }

    private static ShiftCategory Expected(int hour)
    {
        var start = new TimeOnly(hour, 0);
        return ShiftTypeInference.FromSpan(start, start.AddHours(ShiftLengthHours)) switch
        {
            ShiftTypeInference.EarlyIndex => ShiftCategory.Early,
            ShiftTypeInference.LateIndex => ShiftCategory.Late,
            _ => ShiftCategory.Night,
        };
    }

    private static ScheduleCell Cell(int startHour)
        => Cell(new TimeSpan(startHour, 0, 0), new TimeSpan((startHour + ShiftLengthHours) % 24, 0, 0));

    private static ScheduleCell Cell(TimeSpan start, TimeSpan end) => new()
    {
        Id = Guid.NewGuid(),
        EntryType = 0,
        ClientId = ClientId,
        EntryDate = new DateTime(2026, 6, 3),
        StartTime = start,
        EndTime = end,
    };
}
