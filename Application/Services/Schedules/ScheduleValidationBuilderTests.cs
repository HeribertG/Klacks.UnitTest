using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Services.Schedules;
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

    private static SchedulingPolicy Policy(double maxWeeklyHours = 50, int minRestDays = 2)
        => new(
            MinRestHours: TimeSpan.FromHours(11),
            MaxDailyHours: TimeSpan.FromHours(10),
            MaxConsecutiveDays: 6,
            MaxWeeklyHours: TimeSpan.FromHours(maxWeeklyHours),
            MinRestDays: minRestDays);

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
}
