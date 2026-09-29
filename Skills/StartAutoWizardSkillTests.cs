// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for StartAutoWizardSkill - parameter validation, group lookup by id or name, calendar-week
/// resolution, the up-front notice when the holistic harmonization cannot run, and agent/shift
/// auto-resolution. The actual AutoWizardJobRunner is fully mocked.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class StartAutoWizardSkillTests
{
    private IAutoWizardJobRunner _runner = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupPlanningAgentRepository _planningAgentRepository = null!;
    private IShiftScheduleRepository _shiftScheduleRepository = null!;
    private IGroupScopeGuard _groupScopeGuard = null!;
    private ICompanyClock _companyClock = null!;
    private IHolisticHarmonizerReadinessCheck _readinessCheck = null!;
    private StartAutoWizardSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _runner = Substitute.For<IAutoWizardJobRunner>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _planningAgentRepository = Substitute.For<IGroupPlanningAgentRepository>();
        _shiftScheduleRepository = Substitute.For<IShiftScheduleRepository>();
        _groupScopeGuard = Substitute.For<IGroupScopeGuard>();
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 9, 29));
        _readinessCheck = Substitute.For<IHolisticHarmonizerReadinessCheck>();
        _readinessCheck.CheckAsync(Arg.Any<CancellationToken>()).Returns(HolisticHarmonizerReadiness.Ready());

        _skill = new StartAutoWizardSkill(
            _runner, _groupRepository, _planningAgentRepository, _shiftScheduleRepository,
            _groupScopeGuard, _companyClock, _readinessCheck);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { "Admin" }
        };
    }

    [Test]
    public async Task ExecuteAsync_InvalidGroupId_ReturnsError()
    {
        var parameters = new Dictionary<string, object>
        {
            { "groupId", "not-a-uuid" },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid groupId");
    }

    [Test]
    public async Task ExecuteAsync_PeriodFromAfterPeriodUntil_ReturnsError()
    {
        var parameters = new Dictionary<string, object>
        {
            { "groupId", Guid.NewGuid().ToString() },
            { "periodFrom", "2026-05-31" },
            { "periodUntil", "2026-05-01" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must be on or before");
    }

    [Test]
    public async Task ExecuteAsync_GroupNotFound_ReturnsError()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns((Group?)null);

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task ExecuteAsync_NoAgentsResolved_ReturnsError()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId, Name = "Bern" });
        _planningAgentRepository.GetAgentIdsAsync(
            groupId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No agents resolved");
    }

    [Test]
    public async Task ExecuteAsync_NoShiftsResolved_ReturnsError()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId, Name = "Bern" });
        _planningAgentRepository.GetAgentIdsAsync(
            groupId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });
        _shiftScheduleRepository.GetShiftScheduleAsync(
            Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>(), 0));

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No shifts visible");
    }

    [Test]
    public async Task ExecuteAsync_HappyPath_ReturnsJobIdFromRunner()
    {
        var groupId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _groupRepository.Get(groupId).Returns(new Group { Id = groupId, Name = "Bern" });
        _planningAgentRepository.GetAgentIdsAsync(
            groupId, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { clientId });
        _shiftScheduleRepository.GetShiftScheduleAsync(
            Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment> { new() { ShiftId = shiftId } }, 1));
        _runner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns(jobId);

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeTrue();
        result.Data.ShouldNotBeNull();
        await _runner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(r =>
                r.GroupId == groupId &&
                r.PeriodFrom == new DateOnly(2026, 5, 1) &&
                r.PeriodUntil == new DateOnly(2026, 5, 31) &&
                r.AgentIds.Count == 1 &&
                r.ShiftIds!.Count == 1),
            Arg.Any<CancellationToken>());
    }

    private (Guid GroupId, Guid JobId) ArrangeRunnableGroup(string groupName)
    {
        var groupId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var group = new Group { Id = groupId, Name = groupName };

        _groupRepository.Get(groupId).Returns(group);
        _groupRepository.List().Returns(new List<Group> { group, new() { Id = Guid.NewGuid(), Name = "Bern" } });
        _planningAgentRepository.GetAgentIdsAsync(
            groupId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });
        _shiftScheduleRepository.GetShiftScheduleAsync(
            Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment> { new() { ShiftId = Guid.NewGuid() } }, 1));
        _runner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns(jobId);

        return (groupId, jobId);
    }

    [Test]
    public async Task ExecuteAsync_GroupName_ResolvesTheGroupWithoutAnId()
    {
        var (groupId, _) = ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "periodFrom", "2026-11-02" },
            { "periodUntil", "2026-11-08" }
        });

        result.Success.ShouldBeTrue(result.Message);
        await _runner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(r => r.GroupId == groupId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_UnknownGroupName_ReturnsTheRealGroupsInsteadOfStarting()
    {
        ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Zuerich Nord" },
            { "periodFrom", "2026-11-02" },
            { "periodUntil", "2026-11-08" }
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Winterthur");
        await _runner.DidNotReceive().StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_NeitherGroupIdNorGroupName_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "calendarWeek", 45 }
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("groupName");
    }

    [Test]
    public async Task ExecuteAsync_GroupOutsideScope_IsRefused()
    {
        var (groupId, _) = ArrangeRunnableGroup("Winterthur");
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted(new[] { Guid.NewGuid() }, new[] { "Bern" }));

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "calendarWeek", 45 }
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("outside your assigned group scope");
    }

    [Test]
    public async Task ExecuteAsync_CalendarWeek_ResolvesMondayToSundayOfTheCurrentIsoYear()
    {
        ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "calendarWeek", 45 }
        });

        result.Success.ShouldBeTrue(result.Message);
        await _runner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(r =>
                r.PeriodFrom == new DateOnly(2026, 11, 2) &&
                r.PeriodUntil == new DateOnly(2026, 11, 8)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_CalendarWeekWithYear_UsesThatYearAndWinsOverDates()
    {
        ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "calendarWeek", "1" },
            { "year", "2027" },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        });

        result.Success.ShouldBeTrue(result.Message);
        await _runner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(r =>
                r.PeriodFrom == new DateOnly(2027, 1, 4) &&
                r.PeriodUntil == new DateOnly(2027, 1, 10)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_CalendarWeekThatDoesNotExist_ReturnsError()
    {
        ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "calendarWeek", 53 },
            { "year", 2025 }
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("does not exist");
    }

    private async Task<SkillResult> RunCalendarWeekAsync(DateOnly today, object calendarWeek, object? year = null)
    {
        _companyClock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(today);
        ArrangeRunnableGroup("Winterthur");
        var parameters = new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "calendarWeek", calendarWeek }
        };
        if (year != null)
        {
            parameters["year"] = year;
        }

        return await _skill.ExecuteAsync(_context, parameters);
    }

    private Task StartedWithPeriodAsync(DateOnly from, DateOnly until) =>
        _runner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(r => r.PeriodFrom == from && r.PeriodUntil == until),
            Arg.Any<CancellationToken>());

    [Test]
    public async Task ExecuteAsync_CalendarWeekAlreadyPastWithoutYear_PlansItsNextOccurrenceNextYear()
    {
        var result = await RunCalendarWeekAsync(new DateOnly(2026, 12, 10), 2);

        result.Success.ShouldBeTrue(result.Message);
        await StartedWithPeriodAsync(new DateOnly(2027, 1, 11), new DateOnly(2027, 1, 17));
    }

    [Test]
    public async Task ExecuteAsync_NewYearsDayInIsoWeek53_Week1IsTheComingWeekOfTheNewIsoYear()
    {
        var result = await RunCalendarWeekAsync(new DateOnly(2027, 1, 1), 1);

        result.Success.ShouldBeTrue(result.Message);
        await StartedWithPeriodAsync(new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 10));
    }

    [Test]
    public async Task ExecuteAsync_NewYearsDayInIsoWeek53_Week53IsTheCurrentWeek()
    {
        var result = await RunCalendarWeekAsync(new DateOnly(2027, 1, 1), 53);

        result.Success.ShouldBeTrue(result.Message);
        await StartedWithPeriodAsync(new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 3));
    }

    [Test]
    public async Task ExecuteAsync_Week53InAYearWithoutWeek53_ReturnsError()
    {
        var implicitYear = await RunCalendarWeekAsync(new DateOnly(2027, 6, 1), 53);
        var explicitYear = await RunCalendarWeekAsync(new DateOnly(2026, 9, 29), 53, 2027);

        implicitYear.Success.ShouldBeFalse();
        implicitYear.Message.ShouldContain("does not exist in 2027");
        explicitYear.Success.ShouldBeFalse();
        explicitYear.Message.ShouldContain("does not exist in 2027");
        await _runner.DidNotReceive().StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_TwoDigitYear_IsReadAsThe2000s()
    {
        var result = await RunCalendarWeekAsync(new DateOnly(2026, 9, 29), 45, "26");

        result.Success.ShouldBeTrue(result.Message);
        await StartedWithPeriodAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8));
    }

    [TestCase(2030)]
    [TestCase(2024)]
    [TestCase(31)]
    [TestCase(9999)]
    [TestCase(-1)]
    public async Task ExecuteAsync_YearOutsideTheWindow_ReturnsErrorInsteadOfPlanning(int year)
    {
        var result = await RunCalendarWeekAsync(new DateOnly(2026, 9, 29), 45, year);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("between 2025 and 2027");
        await _runner.DidNotReceive().StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>());
    }

    [TestCase(0)]
    [TestCase(54)]
    public async Task ExecuteAsync_WeekNumberOutOfRange_ReturnsError(int week)
    {
        var result = await RunCalendarWeekAsync(new DateOnly(2026, 9, 29), week);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("does not exist");
    }

    [Test]
    public async Task ExecuteAsync_UnparseablePeriodFrom_ReturnsFormatError()
    {
        ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "periodFrom", "next monday" },
            { "periodUntil", "2026-11-08" }
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid periodFrom");
    }

    [Test]
    public async Task ExecuteAsync_NoPeriodAtAll_ReturnsError()
    {
        ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" }
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("calendarWeek");
    }

    [Test]
    public async Task ExecuteAsync_HolisticHarmonizerNotReady_TellsUpFrontThatStageThreeIsSkipped()
    {
        ArrangeRunnableGroup("Winterthur");
        _readinessCheck.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(HolisticHarmonizerReadiness.NotReady(HolisticHarmonizerReadinessCheck.ModelNotConfiguredReason));

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "calendarWeek", 45 }
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("will be skipped");
        result.Message.ShouldContain(HolisticHarmonizerReadinessCheck.ModelNotConfiguredReason);
        result.Message.ShouldNotContain("Stages: planning, harmonizing and holistic harmonization.");
    }

    [Test]
    public async Task ExecuteAsync_HolisticHarmonizerReady_AnnouncesAllThreeStages()
    {
        ArrangeRunnableGroup("Winterthur");

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "groupName", "Winterthur" },
            { "calendarWeek", 45 }
        });

        result.Message.ShouldContain("holistic harmonization");
        result.Message.ShouldNotContain("will be skipped");
    }
}
