using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseLagDetectorTests
{
    private IGroupRepository _groupRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private ISettingsReader _settingsReader = null!;
    private List<Group> _groups = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _activityProbe.HasWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var date = ci.Arg<DateOnly>();
                return date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
            });
        _groups = new List<Group>
        {
            MakeGroup(PaymentInterval.Weekly, "Weekly"),
            MakeGroup(PaymentInterval.Biweekly, "Biweekly"),
            MakeGroup(PaymentInterval.Monthly, "Monthly"),
            MakeGroup(PaymentInterval.MonthlyTargetHours, "Target"),
            MakeGroup(PaymentInterval.Individual, "Individual")
        };
        _groupRepository.List().Returns(_groups);
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(_groups.Select(group => group.Id).ToList());
    }

    private static Group MakeGroup(PaymentInterval interval, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    private void StubLag(string? value) =>
        _settingsReader.GetSetting(SettingKeys.PeriodCloseLagDays).Returns(Task.FromResult<SettingsRow?>(
            value == null ? null : new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = value }));

    private static FixedCompanyClock ClockOn(DateOnly today) =>
        new(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));

    private PeriodCloseDueDetector CloseDueOn(DateOnly today) => new(
        _groupRepository, _sealedDayRepository, _weekConfiguration, _activityProbe,
        NullLogger<PeriodCloseDueDetector>.Instance, ClockOn(today), _settingsReader);

    private PeriodOverdueDetector OverdueOn(DateOnly today) => new(
        _groupRepository, _sealedDayRepository, _weekConfiguration, _activityProbe,
        NullLogger<PeriodOverdueDetector>.Instance, ClockOn(today), _settingsReader);

    private static string Fingerprint(IAgentTriggerEvent evt) => string.Join(
        "|",
        evt.Kind, evt.GroupId, evt.DedupKey, evt.Severity, evt.Summary,
        JsonSerializer.Serialize(evt.SummaryParams),
        JsonSerializer.Serialize(evt.Payload),
        evt.ActionRoute,
        JsonSerializer.Serialize(evt.ActionParams));

    private static IEnumerable<DateOnly> ScanDays()
    {
        var day = new DateOnly(2025, 12, 20);
        var last = new DateOnly(2026, 3, 10);
        for (; day <= last; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    private async Task<List<string>> CloseDueFingerprintsAsync(string? lag)
    {
        StubLag(lag);
        var all = new List<string>();
        foreach (var day in ScanDays())
        {
            var events = await CloseDueOn(day).DetectAsync();
            all.AddRange(events.Select(evt => $"{day:yyyy-MM-dd}#{Fingerprint(evt)}"));
        }

        return all;
    }

    private async Task<List<string>> OverdueFingerprintsAsync(string? lag)
    {
        StubLag(lag);
        var all = new List<string>();
        foreach (var day in ScanDays())
        {
            var events = await OverdueOn(day).DetectAsync();
            all.AddRange(events.Select(evt => $"{day:yyyy-MM-dd}#{Fingerprint(evt)}"));
        }

        return all;
    }

    private PeriodCloseDueTriggerEvent[] CloseDueEventsOf(IReadOnlyList<IAgentTriggerEvent> events, string groupName)
    {
        var id = _groups.Single(group => group.Name == groupName).Id;
        return events.OfType<PeriodCloseDueTriggerEvent>().Where(evt => evt.GroupId == id).ToArray();
    }

    private PeriodOverdueTriggerEvent[] OverdueEventsOf(IReadOnlyList<IAgentTriggerEvent> events, string groupName)
    {
        var id = _groups.Single(group => group.Name == groupName).Id;
        return events.OfType<PeriodOverdueTriggerEvent>().Where(evt => evt.GroupId == id).ToArray();
    }

    [Test]
    public async Task CloseDue_LagZeroStored_IsByteIdenticalToNoSettingOverAWholeScanPeriod()
    {
        var withoutSetting = await CloseDueFingerprintsAsync(null);
        var withZero = await CloseDueFingerprintsAsync("0");

        Assert.That(withoutSetting, Is.Not.Empty);
        Assert.That(withZero, Is.EqualTo(withoutSetting));
    }

    [TestCase("abc")]
    [TestCase("-3")]
    [TestCase("32")]
    [TestCase("")]
    public async Task CloseDue_UnusableStoredValue_BehavesLikeNoSetting(string stored)
    {
        var withoutSetting = await CloseDueFingerprintsAsync(null);
        var withStored = await CloseDueFingerprintsAsync(stored);

        Assert.That(withStored, Is.EqualTo(withoutSetting));
    }

    [Test]
    public async Task Overdue_LagZeroStored_IsByteIdenticalToNoSettingOverAWholeScanPeriod()
    {
        var withoutSetting = await OverdueFingerprintsAsync(null);
        var withZero = await OverdueFingerprintsAsync("0");

        Assert.That(withoutSetting, Is.Not.Empty);
        Assert.That(withZero, Is.EqualTo(withoutSetting));
    }

    [TestCase("abc")]
    [TestCase("32")]
    public async Task Overdue_UnusableStoredValue_BehavesLikeNoSetting(string stored)
    {
        var withoutSetting = await OverdueFingerprintsAsync(null);
        var withStored = await OverdueFingerprintsAsync(stored);

        Assert.That(withStored, Is.EqualTo(withoutSetting));
    }

    [Test]
    public async Task CloseDue_NoLag_EventCarriesNoLagInformation()
    {
        StubLag(null);

        var events = await CloseDueOn(new DateOnly(2026, 1, 29)).DetectAsync();

        var monthly = CloseDueEventsOf(events, "Monthly").Single();
        Assert.That(monthly.LagDays, Is.EqualTo(0));
        Assert.That(monthly.CloseDate, Is.EqualTo(monthly.PeriodEndDate));
        Assert.That(monthly.Payload.ContainsKey("lagDays"), Is.False);
        Assert.That(monthly.Payload.ContainsKey("closeDate"), Is.False);
    }

    [Test]
    public async Task CloseDue_MonthlyWithLag_WarnsThreeDaysBeforeTheCloseDateNotThePeriodEnd()
    {
        StubLag("5");

        var tooEarly = await CloseDueOn(new DateOnly(2026, 1, 29)).DetectAsync();
        var firstDay = await CloseDueOn(new DateOnly(2026, 2, 2)).DetectAsync();

        Assert.That(CloseDueEventsOf(tooEarly, "Monthly"), Is.Empty,
            "the period end is 2 days away but the close date is 7 days away");
        var evt = CloseDueEventsOf(firstDay, "Monthly").Single();
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 31)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(3));
        Assert.That(evt.LagDays, Is.EqualTo(5));
        Assert.That(evt.CloseDate, Is.EqualTo(new DateOnly(2026, 2, 5)));
    }

    [Test]
    public async Task CloseDue_MonthlyWithLag_ReportsAPeriodThatHasAlreadyEnded()
    {
        StubLag("5");

        var events = await CloseDueOn(new DateOnly(2026, 2, 4)).DetectAsync();

        var evt = CloseDueEventsOf(events, "Monthly").Single();
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 31)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(1));
        Assert.That(evt.Payload["lagDays"], Is.EqualTo(5));
        Assert.That(evt.Payload["closeDate"], Is.EqualTo(new DateOnly(2026, 2, 5)));
        Assert.That(evt.ActionParams![ProactiveActionParamKeys.Date], Is.EqualTo("2026-01-31"));
    }

    [Test]
    public async Task CloseDue_OnTheCloseDate_StillFiresWithZeroDaysAndNotOneDayLater()
    {
        StubLag("5");

        var onDay = await CloseDueOn(new DateOnly(2026, 2, 5)).DetectAsync();
        var nextDay = await CloseDueOn(new DateOnly(2026, 2, 6)).DetectAsync();

        Assert.That(CloseDueEventsOf(onDay, "Monthly").Single().DaysUntilDue, Is.EqualTo(0));
        Assert.That(CloseDueEventsOf(nextDay, "Monthly"), Is.Empty);
    }

    [Test]
    public async Task CloseDue_WeeklyWithLag_FollowsTheConfiguredWeekOfTheReferenceDay()
    {
        StubLag("2");

        var events = await CloseDueOn(new DateOnly(2026, 1, 13)).DetectAsync();

        var evt = CloseDueEventsOf(events, "Weekly").Single();
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 11)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(0));
    }

    [Test]
    public async Task CloseDue_SealedPeriodWithLag_IsNotReported()
    {
        StubLag("5");
        var monthly = _groups.Single(group => group.Name == "Monthly");
        _sealedDayRepository.GetRangeAsync(
                new DateOnly(2026, 1, 31), new DateOnly(2026, 1, 31), monthly.Id, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() });

        var events = await CloseDueOn(new DateOnly(2026, 2, 3)).DetectAsync();

        Assert.That(CloseDueEventsOf(events, "Monthly"), Is.Empty);
    }

    [Test]
    public async Task CloseDue_ActiveFingerprintsFollowTheSameWindowAsTheEvents()
    {
        StubLag("5");
        var detector = CloseDueOn(new DateOnly(2026, 2, 3));

        var events = await detector.DetectAsync();
        var fingerprints = await detector.GetActiveFingerprintsAsync();

        var monthly = CloseDueEventsOf(events, "Monthly").Single();
        Assert.That(fingerprints, Does.Contain(
            AgentConditionLedgerPolicy.FingerprintFor(
                detector.Kind, PeriodCloseDueTriggerEvent.DedupKeyFor(monthly.GroupId, monthly.PeriodEndDate))));
    }

    [Test]
    public async Task CloseDue_IndividualGroup_IsNeverReportedWhateverTheLag()
    {
        StubLag("5");

        foreach (var day in ScanDays())
        {
            var events = await CloseDueOn(day).DetectAsync();
            Assert.That(CloseDueEventsOf(events, "Individual"), Is.Empty);
        }
    }

    [Test]
    public async Task Overdue_MonthlyWithLag_CountsSevenDaysFromTheCloseDate()
    {
        StubLag("5");

        var notYet = await OverdueOn(new DateOnly(2026, 2, 11)).DetectAsync();
        var due = await OverdueOn(new DateOnly(2026, 2, 12)).DetectAsync();

        Assert.That(OverdueEventsOf(notYet, "Monthly"), Is.Empty);
        var evt = OverdueEventsOf(due, "Monthly").Single();
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 31)));
        Assert.That(evt.DaysOverdue, Is.EqualTo(7));
        Assert.That(evt.LagDays, Is.EqualTo(5));
        Assert.That(evt.Payload["lagDays"], Is.EqualTo(5));
        Assert.That(evt.SummaryParams["days"], Is.EqualTo("12"),
            "the message says how long ago the period ended, which is the overdue days plus the lag");
    }

    [Test]
    public async Task Overdue_MonthlyWithLag_DoesNotReportThePeriodThatHasNotReachedItsCloseDateYet()
    {
        StubLag("10");

        var events = await OverdueOn(new DateOnly(2026, 3, 5)).DetectAsync();

        var evt = OverdueEventsOf(events, "Monthly").Single();
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 31)),
            "February ended on the 28th, its close date is March 10, so January is still the overdue one");
    }

    [Test]
    public async Task Overdue_NoLag_EventCarriesNoLagInformation()
    {
        StubLag(null);

        var events = await OverdueOn(new DateOnly(2026, 2, 10)).DetectAsync();

        var evt = OverdueEventsOf(events, "Monthly").Single();
        Assert.That(evt.LagDays, Is.EqualTo(0));
        Assert.That(evt.Payload.ContainsKey("lagDays"), Is.False);
        Assert.That(evt.SummaryParams["days"], Is.EqualTo(evt.DaysOverdue.ToString()));
    }

    [Test]
    public async Task Overdue_IndividualGroup_IsNeverReportedWhateverTheLag()
    {
        StubLag("5");

        foreach (var day in ScanDays())
        {
            var events = await OverdueOn(day).DetectAsync();
            Assert.That(OverdueEventsOf(events, "Individual"), Is.Empty);
        }
    }
}
