using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.UnitTest.TestHelpers;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetPlanningDeadlineSkillTests
{
    private static readonly DateOnly Today = new(2026, 1, 10);

    private IGroupRepository _groupRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private ISettingsReader _settingsReader = null!;
    private GetPlanningDeadlineSkill _sut = null!;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "Admin" }
    };

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var date = ci.Arg<DateOnly>();
                return date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
            });
        var clock = new FixedCompanyClock(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));

        _sut = new GetPlanningDeadlineSkill(_groupRepository, _weekConfiguration, _settingsReader, clock);
    }

    private void StubGroups(params Group[] groups)
    {
        _groupRepository.List().Returns(groups.ToList());
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(groups.Select(g => g.Id).ToList());
    }

    private void StubSetting(string key, string? value) =>
        _settingsReader.GetSetting(key).Returns(Task.FromResult<SettingsRow?>(
            value == null ? null : new SettingsRow { Type = key, Value = value }));

    private static Group MakeGroup(PaymentInterval interval, string name = "Bern") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
    };

    private static string DataOf(SkillResult result) => System.Text.Json.JsonSerializer.Serialize(result.Data);

    private static Dictionary<string, object> Args(int announcement, int? transit, int review, string channel = "email")
    {
        var args = new Dictionary<string, object>
        {
            [PlanningDeadlineParameters.DeliveryChannel] = channel,
            [PlanningDeadlineParameters.AnnouncementDays] = announcement,
            [PlanningDeadlineParameters.ReviewDays] = review
        };
        if (transit.HasValue)
        {
            args[PlanningDeadlineParameters.TransitDays] = transit.Value;
        }

        return args;
    }

    [Test]
    public async Task ExecuteAsync_NoNumbers_ReportsContextWithComplianceMinimumAndStoredLead()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubSetting(SettingKeys.ComplianceRosterPublicationMinLeadDays, "14");
        StubSetting(SettingKeys.PlanningDeadlineLeadDays, "19");

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        Assert.That(DataOf(result), Does.Contain("\"Mode\":\"Context\""));
        Assert.That(DataOf(result), Does.Contain("\"ComplianceMinLeadDays\":14"));
        Assert.That(DataOf(result), Does.Contain("\"StoredDeadlineLeadDays\":19"));
    }

    [Test]
    public async Task ExecuteAsync_OnlyChannelAndTransitCollectedSoFar_StillReportsTheContext()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        var args = new Dictionary<string, object>
        {
            [PlanningDeadlineParameters.DeliveryChannel] = "email",
            [PlanningDeadlineParameters.TransitDays] = 0
        };

        var result = await _sut.ExecuteAsync(Ctx(), args);

        Assert.That(result.Success, Is.True);
        Assert.That(DataOf(result), Does.Contain("\"Mode\":\"Context\""));
    }

    [Test]
    public async Task ExecuteAsync_PostWithoutTransitDays_AsksForThemInsteadOfAssumingZero()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));

        var result = await _sut.ExecuteAsync(Ctx(), Args(announcement: 14, transit: null, review: 2, channel: "post"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain(PlanningDeadlineParameters.TransitDays));
    }

    [Test]
    public async Task ExecuteAsync_EmailMonthlyGroup_ComputesTheDeadlinePerGroup()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));

        var result = await _sut.ExecuteAsync(Ctx(), Args(announcement: 14, transit: null, review: 2));

        Assert.That(result.Success, Is.True);
        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"Mode\":\"Computed\""));
        Assert.That(data, Does.Contain("\"DeadlineLeadDays\":16"));
        Assert.That(data, Does.Contain("\"PeriodStart\":\"2026-02-01\""));
        Assert.That(data, Does.Contain("\"PlanningDoneBy\":\"2026-01-16\""));
        Assert.That(data, Does.Contain("\"DaysRemaining\":6"));
        Assert.That(data, Does.Contain("\"NextPeriodOverdue\":false"));
    }

    [Test]
    public async Task ExecuteAsync_Post_IncludesTransitInTheLead()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));

        var result = await _sut.ExecuteAsync(Ctx(), Args(14, transit: 3, review: 2, channel: "post"));

        Assert.That(DataOf(result), Does.Contain("\"DeadlineLeadDays\":19"));
        Assert.That(DataOf(result), Does.Contain("\"PlanningDoneBy\":\"2026-01-13\""));
    }

    [Test]
    public async Task ExecuteAsync_ComplianceMinimumIsAFloorForTheAnnouncement()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubSetting(SettingKeys.ComplianceRosterPublicationMinLeadDays, "14");

        var result = await _sut.ExecuteAsync(Ctx(), Args(announcement: 7, transit: null, review: 2));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"AnnouncementDays\":14"));
        Assert.That(data, Does.Contain("\"AnnouncementFloorApplied\":true"));
        Assert.That(data, Does.Contain("\"ComplianceMinLeadDays\":14"));
        Assert.That(data, Does.Contain("\"StoredDeadlineLeadDays\":0"));
        Assert.That(data, Does.Contain("\"DeadlineLeadDays\":16"));
    }

    [Test]
    public async Task ExecuteAsync_IndividualAndUnstaffedGroups_AreLeftOut()
    {
        var staffed = MakeGroup(PaymentInterval.Monthly, "Bern");
        var individual = MakeGroup(PaymentInterval.Individual, "Custom");
        var unstaffed = MakeGroup(PaymentInterval.Monthly, "Empty");
        _groupRepository.List().Returns(new List<Group> { staffed, individual, unstaffed });
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { staffed.Id, individual.Id });

        var result = await _sut.ExecuteAsync(Ctx(), Args(14, null, 2));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("Bern"));
        Assert.That(data, Does.Not.Contain("Custom"));
        Assert.That(data, Does.Not.Contain("Empty"));
    }

    [Test]
    public async Task ExecuteAsync_WeeklyGroupWithLongLead_ReportsTheFirstReachablePeriodAndFlagsTheMissedOne()
    {
        StubGroups(MakeGroup(PaymentInterval.Weekly));

        var result = await _sut.ExecuteAsync(Ctx(), Args(announcement: 14, transit: null, review: 2));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"NextPeriodStart\":\"2026-01-12\""));
        Assert.That(data, Does.Contain("\"NextPeriodOverdue\":true"));
        Assert.That(data, Does.Contain("\"PeriodStart\":\"2026-01-26\""));
    }

    [TestCase(-1, 0, 2)]
    [TestCase(14, 400, 2)]
    [TestCase(14, 0, -5)]
    public async Task ExecuteAsync_OutOfRangeInput_IsRejectedWithoutComputing(int announcement, int transit, int review)
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));

        var result = await _sut.ExecuteAsync(Ctx(), Args(announcement, transit, review));

        Assert.That(result.Success, Is.False);
        await _groupRepository.DidNotReceive().GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_AnnouncementWithoutReview_AsksForTheMissingValue()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        var args = new Dictionary<string, object> { [PlanningDeadlineParameters.AnnouncementDays] = 14 };

        var result = await _sut.ExecuteAsync(Ctx(), args);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain(PlanningDeadlineParameters.ReviewDays));
    }
}
