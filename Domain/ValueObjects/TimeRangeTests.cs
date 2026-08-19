using Klacks.Api.Domain.ValueObjects;
using Shouldly;

namespace Klacks.UnitTest.Domain.ValueObjects;

[TestFixture]
public class TimeRangeTests
{
    [Test]
    public void WorkingTime_StartEqualsEnd_IsAFullDay()
    {
        TimeRange.ForWorkingTime(new TimeOnly(7, 0), new TimeOnly(7, 0))
            .DurationInHours.ShouldBe(24m);
    }

    [Test]
    public void WorkingTime_MidnightToMidnight_IsAFullDay()
    {
        TimeRange.ForWorkingTime(new TimeOnly(0, 0), new TimeOnly(0, 0))
            .DurationInHours.ShouldBe(24m);
    }

    [Test]
    public void WorkingTime_CrossingMidnight_MeasuresExactly()
    {
        TimeRange.ForWorkingTime(new TimeOnly(22, 0), new TimeOnly(6, 0))
            .Duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Test]
    public void WorkingTime_OrdinarySpan_MeasuresExactly()
    {
        TimeRange.ForWorkingTime(new TimeOnly(8, 0), new TimeOnly(15, 30))
            .DurationInHours.ShouldBe(7.5m);
    }

    [Test]
    public void AbsenceRecording_StartEqualsEnd_CreditsNothing()
    {
        TimeRange.ForAbsenceRecording(new TimeOnly(0, 0), new TimeOnly(0, 0))
            .DurationInHours.ShouldBe(0m);
    }

    [Test]
    public void AbsenceRecording_StillWrapsARealMidnightSpan()
    {
        TimeRange.ForAbsenceRecording(new TimeOnly(22, 0), new TimeOnly(6, 0))
            .Duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Test]
    public void TheTwoConventionsDisagreeOnlyOnEqualBounds()
    {
        var working = TimeRange.ForWorkingTime(new TimeOnly(12, 0), new TimeOnly(12, 0));
        var absence = TimeRange.ForAbsenceRecording(new TimeOnly(12, 0), new TimeOnly(12, 0));

        working.DurationInHours.ShouldBe(24m);
        absence.DurationInHours.ShouldBe(0m);
    }

    [Test]
    public void FullDayMarker_IsTheExactBookingPair()
    {
        TimeRange.ForAbsenceRecording(new TimeOnly(0, 0), new TimeOnly(23, 59))
            .IsFullDayMarker.ShouldBeTrue();
    }

    [Test]
    public void FullDayMarker_DoesNotSwallowASpanStartingLateAtNight()
    {
        TimeRange.ForAbsenceRecording(new TimeOnly(23, 54), new TimeOnly(23, 59))
            .IsFullDayMarker.ShouldBeFalse();
    }

    [Test]
    public void FullDayMarker_IsNotTheEqualBoundsPair()
    {
        TimeRange.ForWorkingTime(new TimeOnly(0, 0), new TimeOnly(0, 0))
            .IsFullDayMarker.ShouldBeFalse();
    }

    [Test]
    public void WorkingTime_EqualBounds_ContainsEveryTimeOfDay()
    {
        var fullDay = TimeRange.ForWorkingTime(new TimeOnly(7, 0), new TimeOnly(7, 0));

        fullDay.Contains(new TimeOnly(3, 0)).ShouldBeTrue();
        fullDay.Contains(new TimeOnly(18, 0)).ShouldBeTrue();
    }

    [Test]
    public void EqualBoundsMeaning_IsPartOfIdentity()
    {
        var working = TimeRange.ForWorkingTime(new TimeOnly(9, 0), new TimeOnly(9, 0));
        var absence = TimeRange.ForAbsenceRecording(new TimeOnly(9, 0), new TimeOnly(9, 0));

        working.ShouldNotBe(absence);
    }
}
