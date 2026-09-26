using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetPeriodCloseScheduleSkillTests
{
    private static readonly DateOnly Today = new(2026, 1, 10);

    private IGroupRepository _groupRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private ISettingsReader _settingsReader = null!;
    private IProactiveGovernanceResolver _governanceResolver = null!;
    private IPeriodAutoCloseResolver _autoCloseResolver = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private GetPeriodCloseScheduleSkill _sut = null!;

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
        _governanceResolver = Substitute.For<IProactiveGovernanceResolver>();
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Autonomous);
        _autoCloseResolver = Substitute.For<IPeriodAutoCloseResolver>();
        StubAutoClose(PeriodAutoCloseBlockedBy.GlobalLevel);
        _groupRepository.List().Returns(new List<Group>());
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var date = ci.Arg<DateOnly>();
                return date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
            });
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _sealedDayRepository.GetRangeAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _activityProbe.HasDirectWorkInRangeAsync(
                Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var clock = new FixedCompanyClock(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));

        _sut = new GetPeriodCloseScheduleSkill(
            _groupRepository, _weekConfiguration, _settingsReader, _governanceResolver, _autoCloseResolver, clock,
            _sealedDayRepository, _activityProbe);
    }

    private void StubAutoClose(PeriodAutoCloseBlockedBy blockedBy) =>
        _autoCloseResolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodAutoCloseDecision(
                blockedBy == PeriodAutoCloseBlockedBy.None ? AutonomyLevel.FullyAutonomous : AutonomyLevel.Autonomous,
                Guid.NewGuid(),
                blockedBy == PeriodAutoCloseBlockedBy.None,
                blockedBy));

    private void StubGroups(params Group[] groups)
    {
        _groupRepository.List().Returns(groups.ToList());
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(groups.Select(g => g.Id).ToList());
    }

    private void StubStoredLag(string? value) =>
        _settingsReader.GetSetting(SettingKeys.PeriodCloseLagDays).Returns(Task.FromResult<SettingsRow?>(
            value == null ? null : new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = value }));

    private static Group MakeGroup(PaymentInterval interval, string name = "Bern") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
    };

    private static string DataOf(SkillResult result) => System.Text.Json.JsonSerializer.Serialize(result.Data);

    private static Dictionary<string, object> Args(int lagDays) => new()
    {
        [PeriodCloseParameters.LagDays] = lagDays
    };

    [Test]
    public async Task ExecuteAsync_NoLag_NothingStored_ReportsThatPeriodsAreNeverClosedAutomatically()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"Mode\":\"Context\""));
        Assert.That(data, Does.Contain("\"LagStored\":false"));
        Assert.That(data, Does.Contain("\"StoredLagDays\":null"));
        Assert.That(data, Does.Contain("\"AutonomyAllowsAutoClose\":false"));
        Assert.That(data, Does.Contain("\"Today\":\"2026-01-10\""));
        Assert.That(result.Message, Does.Contain("never closed automatically"));
    }

    [Test]
    public async Task ExecuteAsync_NoLag_StoredZeroIsDifferentFromNothingStored()
    {
        StubStoredLag("0");

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"LagStored\":true"));
        Assert.That(data, Does.Contain("\"StoredLagDays\":0"));
    }

    [Test]
    public async Task ExecuteAsync_NoLag_UnparsableStoredValue_CountsAsNothingStored()
    {
        StubStoredLag("abc");

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(DataOf(result), Does.Contain("\"LagStored\":false"));
    }

    [Test]
    public async Task ExecuteAsync_NoLag_EveryGateOpen_ReportsAutoCloseForTheGroup()
    {
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.FullyAutonomous);
        StubAutoClose(PeriodAutoCloseBlockedBy.None);
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag("2");

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"GlobalAutonomyLevel\":\"FullyAutonomous\""));
        Assert.That(data, Does.Contain("\"AutonomyAllowsAutoClose\":true"));
        Assert.That(data, Does.Contain("\"AutoCloseAllowed\":true"));
        Assert.That(data, Does.Contain("\"AutoCloseBlockedBy\":\"None\""));
    }

    [Test]
    public async Task ExecuteAsync_FullyAutonomousGlobalLevelButGovernanceRuleAtHint_DoesNotPromiseAnAutoClose()
    {
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.FullyAutonomous);
        StubAutoClose(PeriodAutoCloseBlockedBy.MaxAction);
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag("2");

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"AutonomyAllowsAutoClose\":false"));
        Assert.That(data, Does.Contain("\"AutoCloseBlockedBy\":\"MaxAction\""));
        Assert.That(result.Message, Does.Contain("active for no group"));
        Assert.That(result.Message, Does.Contain("period_auto_close"));
    }

    [Test]
    public async Task ExecuteAsync_WithLag_ReportsTheGatePerGroupRow()
    {
        StubAutoClose(PeriodAutoCloseBlockedBy.AdminLevel);
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(3));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"AutoCloseAllowed\":false"));
        Assert.That(data, Does.Contain("\"AutoCloseBlockedBy\":\"AdminLevel\""));
    }

    [Test]
    public async Task ExecuteAsync_MonthlyGroup_ClosesLagDaysAfterTheMonthEnd()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(5));

        Assert.That(result.Success, Is.True);
        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"Mode\":\"Computed\""));
        Assert.That(data, Does.Contain("\"CurrentPeriodEnd\":\"2026-01-31\""));
        Assert.That(data, Does.Contain("\"CurrentPeriodCloseDate\":\"2026-02-05\""));
        Assert.That(data, Does.Contain("\"CurrentPeriodDaysUntilClose\":26"));
    }

    [Test]
    public async Task ExecuteAsync_PreviousPeriodPastItsCloseDateAndStillOpen_IsReportedAsDue()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(5));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"PreviousPeriodEnd\":\"2025-12-31\""));
        Assert.That(data, Does.Contain("\"PreviousPeriodCloseDate\":\"2026-01-05\""));
        Assert.That(data, Does.Contain("\"PreviousPeriodDaysUntilClose\":-5"));
        Assert.That(data, Does.Contain("\"PreviousPeriodCloseDue\":true"));
    }

    [Test]
    public async Task ExecuteAsync_PreviousPeriodAlreadySealed_IsNotReported()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubStoredLag(null);
        _sealedDayRepository.GetRangeAsync(
                new DateOnly(2025, 12, 31), new DateOnly(2025, 12, 31), group.Id, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Date = new DateOnly(2025, 12, 31), GroupId = group.Id } });

        var result = await _sut.ExecuteAsync(Ctx(), Args(20));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"PreviousPeriodCloseDate\":null"));
        Assert.That(data, Does.Contain("\"PreviousPeriodCloseDue\":null"));
    }

    [Test]
    public async Task ExecuteAsync_PreviousPeriodWithoutWork_IsNotReported()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag(null);
        _activityProbe.HasDirectWorkInRangeAsync(
                Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.ExecuteAsync(Ctx(), Args(5));

        Assert.That(DataOf(result), Does.Contain("\"PreviousPeriodCloseDate\":null"));
    }

    [Test]
    public async Task ExecuteAsync_PreviousPeriodCheck_ProbesThePreviousPeriodRange()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag(null);

        await _sut.ExecuteAsync(Ctx(), Args(5));

        await _activityProbe.Received().HasDirectWorkInRangeAsync(
            Arg.Any<Group>(), new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WeeklyGroup_UsesTheConfiguredWeekEnd()
    {
        StubGroups(MakeGroup(PaymentInterval.Weekly));
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(3));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"CurrentPeriodEnd\":\"2026-01-11\""));
        Assert.That(data, Does.Contain("\"CurrentPeriodCloseDate\":\"2026-01-14\""));
        Assert.That(data, Does.Contain("\"CurrentPeriodDaysUntilClose\":4"));
    }

    [Test]
    public async Task ExecuteAsync_LongLag_ListsThePreviousPeriodThatStillWaitsForItsCloseDate()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(20));

        var data = DataOf(result);
        Assert.That(data, Does.Contain("\"PreviousPeriodEnd\":\"2025-12-31\""));
        Assert.That(data, Does.Contain("\"PreviousPeriodCloseDate\":\"2026-01-20\""));
        Assert.That(data, Does.Contain("\"PreviousPeriodDaysUntilClose\":10"));
        Assert.That(data, Does.Contain("\"PreviousPeriodCloseDue\":false"));
    }

    [Test]
    public async Task ExecuteAsync_IndividualGroup_HasNoCloseDateAndIsNotListed()
    {
        StubGroups(MakeGroup(PaymentInterval.Individual, "Custom"), MakeGroup(PaymentInterval.Monthly, "Bern"));
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(2));

        var data = DataOf(result);
        Assert.That(data, Does.Not.Contain("Custom"));
        Assert.That(data, Does.Contain("Bern"));
    }

    [Test]
    public async Task ExecuteAsync_GroupWithoutMembers_IsNotListed()
    {
        var empty = MakeGroup(PaymentInterval.Monthly, "Empty");
        _groupRepository.List().Returns(new List<Group> { empty });
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(2));

        Assert.That(DataOf(result), Does.Not.Contain("Empty"));
    }

    [TestCase(-1)]
    [TestCase(32)]
    public async Task ExecuteAsync_InvalidLag_IsRejected(int lagDays)
    {
        StubStoredLag(null);

        var result = await _sut.ExecuteAsync(Ctx(), Args(lagDays));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain(PeriodCloseParameters.LagDays));
    }
}
