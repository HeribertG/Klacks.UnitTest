using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ScheduleValidationBuilderTests
{
    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly DateOnly Sunday = new(2026, 3, 8);

    private Guid _clientId;
    private ClientTimeline _timeline = null!;
    private List<ScheduleValidationNotificationDto> _entries = null!;

    [SetUp]
    public void Setup()
    {
        _clientId = Guid.NewGuid();
        _timeline = new ClientTimeline(_clientId);
        _entries = [];
    }

    private static SchedulingPolicy Policy(double maxWeeklyHours = 50, decimal minRestDays = 2, TimeSpan? maxDailySpan = null)
        => new(
            MinRestHours: TimeSpan.FromHours(11),
            MaxDailyHours: TimeSpan.FromHours(10),
            MaxConsecutiveDays: 6,
            MaxWeeklyHours: TimeSpan.FromHours(maxWeeklyHours),
            MinRestDays: minRestDays,
            MaxDailySpan: maxDailySpan);

    private void AddWorkDays(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var date = Monday.AddDays(i);
            _timeline.AddBlock(new ScheduleBlock(
                Guid.NewGuid(), ScheduleBlockType.Work, _clientId,
                date.ToDateTime(new TimeOnly(8, 0)),
                date.ToDateTime(new TimeOnly(16, 0))));
        }
        _timeline.SortBlocks();
    }

    [Test]
    public void AddWeeklyOvertime_SevenEightHourDays_FlagsViolation()
    {
        AddWorkDays(7);

        ScheduleValidationBuilder.AddWeeklyOvertime(_entries, _timeline, "Test", Monday, Sunday, Policy());

        _entries.Count.ShouldBe(1);
        _entries[0].Comment.ShouldBe("schedule.error-list.weekly-overtime");
        _entries[0].CommentParams["actualHours"].ShouldBe("56.0");
        _entries[0].CommentParams["maxHours"].ShouldBe("50");
    }

    [Test]
    public void AddWeeklyOvertime_FiveEightHourDays_NoViolation()
    {
        AddWorkDays(5);

        ScheduleValidationBuilder.AddWeeklyOvertime(_entries, _timeline, "Test", Monday, Sunday, Policy());

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void AddMinRestDays_FullWeekNoRest_FlagsViolation()
    {
        AddWorkDays(7);

        ScheduleValidationBuilder.AddMinRestDays(_entries, _timeline, "Test", Monday, Sunday, Policy());

        _entries.Count.ShouldBe(1);
        _entries[0].Comment.ShouldBe("schedule.error-list.min-rest-days");
        _entries[0].CommentParams["actualDays"].ShouldBe("0");
        _entries[0].CommentParams["minDays"].ShouldBe("2");
    }

    [Test]
    public void AddMinRestDays_FullWeekWithTwoRestDays_NoViolation()
    {
        AddWorkDays(5);

        ScheduleValidationBuilder.AddMinRestDays(_entries, _timeline, "Test", Monday, Sunday, Policy());

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void AddMinRestDays_PartialBoundaryWeek_NotEvaluated()
    {
        // Period covers only Monday-Wednesday: an incomplete ISO week must NOT be judged
        // (the MinRestDays-spillover trap), even though all three loaded days are worked.
        AddWorkDays(3);

        ScheduleValidationBuilder.AddMinRestDays(_entries, _timeline, "Test", Monday, Monday.AddDays(2), Policy());

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void AddMinRestDays_FractionalThreshold_OneRestDayViolates()
    {
        // Spain (ET Art. 37.1) requires 1.5 rest days/week on average - one whole rest day must
        // still be flagged (1 >= 1.5 is false).
        AddWorkDays(6);

        ScheduleValidationBuilder.AddMinRestDays(_entries, _timeline, "Test", Monday, Sunday, Policy(minRestDays: 1.5m));

        _entries.Count.ShouldBe(1);
        _entries[0].CommentParams["actualDays"].ShouldBe("1");
        _entries[0].CommentParams["minDays"].ShouldBe("1.5");
    }

    [Test]
    public void AddMinRestDays_FractionalThreshold_TwoRestDaysDoNotViolate()
    {
        AddWorkDays(5);

        ScheduleValidationBuilder.AddMinRestDays(_entries, _timeline, "Test", Monday, Sunday, Policy(minRestDays: 1.5m));

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void AddOvertime_DailyCapStillEvaluated_AfterPolicyExtension()
    {
        var longDay = new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, _clientId,
            Monday.ToDateTime(new TimeOnly(6, 0)),
            Monday.ToDateTime(new TimeOnly(23, 0)));
        _timeline.AddBlock(longDay);
        _timeline.SortBlocks();

        ScheduleValidationBuilder.AddOvertime(_entries, _timeline, "Test", Monday, Monday, Policy());

        _entries.Count.ShouldBe(1);
        _entries[0].Comment.ShouldBe("schedule.error-list.overtime");
    }

    [Test]
    public void AddConsecutiveDays_OverAWeekTimeline_ReportsTheRunOnItsStartDay()
    {
        AddWorkDays(7);
        var (weekStart, weekEnd) = ScheduleValidationBuilder.IsoWeekOf(Monday.AddDays(3));

        ScheduleValidationBuilder.AddConsecutiveDays(_entries, _timeline, "Test", weekStart, weekEnd, Policy());

        _entries.Count.ShouldBe(1);
        _entries[0].Date.ShouldBe(Monday);
        _entries[0].CommentParams["actualDays"].ShouldBe("7");
    }

    [Test]
    public void AddConsecutiveDays_OverASingleDayTimeline_CannotSeeARunAtAll()
    {
        var singleDay = new ClientTimeline(_clientId);
        singleDay.AddBlock(new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, _clientId,
            Monday.ToDateTime(new TimeOnly(8, 0)),
            Monday.ToDateTime(new TimeOnly(16, 0))));
        singleDay.SortBlocks();

        ScheduleValidationBuilder.AddConsecutiveDays(_entries, singleDay, "Test", Monday, Monday, Policy());

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void IsoWeekOf_AnyDayOfTheWeek_ReturnsSameMondayToSundayRange()
    {
        foreach (var offset in Enumerable.Range(0, 7))
        {
            var (start, end) = ScheduleValidationBuilder.IsoWeekOf(Monday.AddDays(offset));

            start.ShouldBe(Monday);
            end.ShouldBe(Sunday);
        }
    }

    [Test]
    public void IsoWeekOf_Sunday_BelongsToTheWeekThatStarted_NotTheNextOne()
    {
        var (start, end) = ScheduleValidationBuilder.IsoWeekOf(Sunday);

        start.ShouldBe(Monday);
        end.ShouldBe(Sunday);
    }

    [Test]
    public void AddWeeklyOvertime_CalledWithIsoWeekBounds_AnchorsEntryOnMonday()
    {
        AddWorkDays(7);
        var (weekStart, weekEnd) = ScheduleValidationBuilder.IsoWeekOf(Monday.AddDays(3));

        ScheduleValidationBuilder.AddWeeklyOvertime(_entries, _timeline, "Test", weekStart, weekEnd, Policy(maxWeeklyHours: 10));

        _entries.Count.ShouldBe(1);
        _entries[0].Date.ShouldBe(Monday);
    }

    [Test]
    public void AddMinRestDays_CalledWithIsoWeekBounds_EvaluatesTheWeek()
    {
        AddWorkDays(7);
        var (weekStart, weekEnd) = ScheduleValidationBuilder.IsoWeekOf(Monday.AddDays(3));

        ScheduleValidationBuilder.AddMinRestDays(_entries, _timeline, "Test", weekStart, weekEnd, Policy());

        _entries.Count.ShouldBe(1);
        _entries[0].Date.ShouldBe(Monday);
    }

    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);
    private static readonly DateOnly Thursday = Monday.AddDays(3);

    private void AddShift(DateOnly date, int startHour, int endHour)
    {
        _timeline.AddBlock(new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, _clientId,
            date.ToDateTime(new TimeOnly(startHour, 0)),
            date.ToDateTime(new TimeOnly(endHour, 0))));
        _timeline.SortBlocks();
    }

    [Test]
    public void AddRestViolations_WindowAroundTuesday_LateMondayEarlyTuesday_ReportsOneEntryDatedMonday()
    {
        AddShift(Monday, 15, 22);
        AddShift(Tuesday, 7, 15);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy(), Monday, Tuesday);

        _entries.Count.ShouldBe(1);
        _entries[0].Date.ShouldBe(Monday);
        _entries[0].Comment.ShouldBe("schedule.error-list.rest-violation");
        _entries[0].CommentParams["actualHours"].ShouldBe("9.0");
        _entries[0].CommentParams["endTime"].ShouldBe("22:00");
        _entries[0].CommentParams["startTime"].ShouldBe("07:00");
    }

    [Test]
    public void AddRestViolations_PairOutsideTheReportWindow_IsNotReported()
    {
        AddShift(Wednesday, 15, 22);
        AddShift(Thursday, 7, 15);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy(), Monday, Tuesday);

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void AddRestViolations_PairWhosePreviousShiftIsOnTheLastWindowDay_IsReported()
    {
        AddShift(Tuesday, 15, 22);
        AddShift(Wednesday, 7, 15);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy(), Monday, Tuesday);

        _entries.Count.ShouldBe(1);
        _entries[0].Date.ShouldBe(Tuesday);
    }

    [Test]
    public void AddRestViolations_PairEntirelyOnThePreviousDay_IsStillReported()
    {
        AddShift(Monday, 6, 10);
        AddShift(Monday, 18, 22);
        AddShift(Tuesday, 7, 15);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy(), Monday, Tuesday);

        _entries.Count.ShouldBe(2);
        _entries.ShouldAllBe(e => e.Date == Monday);
    }

    [Test]
    public void AddRestViolations_OverASingleDayTimeline_CannotSeeAPairAcrossMidnight()
    {
        AddShift(Tuesday, 7, 15);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy());

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void AddRestViolations_WithoutWindow_ReportsEveryPair()
    {
        AddShift(Monday, 15, 22);
        AddShift(Tuesday, 7, 15);
        AddShift(Wednesday, 15, 22);
        AddShift(Thursday, 7, 15);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy());

        _entries.Select(e => e.Date).ShouldBe([Monday, Wednesday]);
    }

    [Test]
    public void DailyWorkFrame_WithoutLegalFrame_Is24HoursMinusMinRest()
    {
        Policy().DailyWorkFrame.ShouldBe(TimeSpan.FromHours(13));
        Policy(maxDailySpan: TimeSpan.Zero).DailyWorkFrame.ShouldBe(TimeSpan.FromHours(13));
    }

    [Test]
    public void DailyWorkFrame_WithLegalFrame_IsTheLegalFrame()
    {
        Policy(maxDailySpan: TimeSpan.FromHours(14)).DailyWorkFrame.ShouldBe(TimeSpan.FromHours(14));
    }

    [Test]
    public void AddRestViolations_ChFrame_SplitShiftWithin14Hours_IsNotReported()
    {
        AddShift(Monday, 7, 11);
        AddShift(Monday, 16, 21);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy(maxDailySpan: TimeSpan.FromHours(14)));

        _entries.ShouldBeEmpty();
    }

    [Test]
    public void AddRestViolations_DefaultFrame_SameSplitShiftBeyond13Hours_IsReported()
    {
        AddShift(Monday, 7, 11);
        AddShift(Monday, 16, 21);

        ScheduleValidationBuilder.AddRestViolations(_entries, _timeline, "Test", Policy());

        _entries.ShouldHaveSingleItem().CommentParams["actualHours"].ShouldBe("5.0");
    }
}
