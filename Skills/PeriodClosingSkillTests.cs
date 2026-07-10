// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the period-closing skills — the group-aware close_period/reopen_period
/// (day-lock verification, mandatory reopen reason, payroll-hook note), get_period_status
/// badge aggregation, list_period_issues pre-flight, list_period_audit_log and
/// list_open_periods with per-period seal state.
/// </summary>

using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PeriodClosingSkillTests
{
    private IMediator _mediator = null!;
    private IGroupRepository _groupRepository = null!;
    private Group _group = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _group = new Group { Id = Guid.NewGuid(), Name = "Verkauf" };
        _groupRepository.Get(_group.Id).Returns(_group);
        _groupRepository.List().Returns(new List<Group> { _group });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewShifts" }
    };

    private static SealedPeriodSummaryDto Day(int day, bool daySealed, int total = 3, int sealedWorks = 0) => new()
    {
        Date = new DateOnly(2026, 6, day),
        TotalWorkCount = total,
        SealedWorkCount = sealedWorks,
        IsDaySealed = daySealed
    };

    private static Dictionary<string, object> Range(string extraKey = "", object? extraValue = null)
    {
        var p = new Dictionary<string, object>
        {
            ["startDate"] = "2026-06-01",
            ["endDate"] = "2026-06-30"
        };
        if (extraKey.Length > 0 && extraValue != null)
        {
            p[extraKey] = extraValue;
        }

        return p;
    }

    [Test]
    public async Task ClosePeriod_WithGroup_SealsViaGroupCommand_AndMentionsPayrollHook()
    {
        _mediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>()).Returns(42);
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedPeriodSummaryDto> { Day(1, true), Day(2, true) });
        var skill = new ClosePeriodSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range("groupName", "Verkauf"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("payroll/ERP export hook was triggered");
        await _mediator.Received(1).Send(
            Arg.Is<ClosePeriodByGroupCommand>(c => c.GroupId == _group.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClosePeriod_Global_WarnsThatNoPayrollExportFires()
    {
        _mediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>()).Returns(10);
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedPeriodSummaryDto> { Day(1, true) });
        var skill = new ClosePeriodSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("No payroll/ERP export fires");
        await _mediator.Received(1).Send(
            Arg.Is<ClosePeriodByGroupCommand>(c => c.GroupId == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClosePeriod_ReturnsError_WhenNoDayLocksAfterSeal()
    {
        _mediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>()).Returns(10);
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedPeriodSummaryDto> { Day(1, false) });
        var skill = new ClosePeriodSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task ReopenPeriod_RefusesWithoutReason()
    {
        var skill = new ReopenPeriodSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("reason is required");
        await _mediator.DidNotReceive().Send(
            Arg.Any<ReopenPeriodByGroupCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReopenPeriod_UnsealsWithReason_AndVerifiesDayLocksGone()
    {
        _mediator.Send(Arg.Any<ReopenPeriodByGroupCommand>(), Arg.Any<CancellationToken>()).Returns(7);
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedPeriodSummaryDto> { Day(1, false), Day(2, false) });
        var skill = new ReopenPeriodSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range("reason", "payroll correction"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<ReopenPeriodByGroupCommand>(c => c.Reason == "payroll correction"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReopenPeriod_ReturnsError_WhenDayLocksRemain()
    {
        _mediator.Send(Arg.Any<ReopenPeriodByGroupCommand>(), Arg.Any<CancellationToken>()).Returns(7);
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedPeriodSummaryDto> { Day(1, true) });
        var skill = new ReopenPeriodSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range("reason", "fix"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task GetPeriodStatus_AggregatesBadges_AndNamesOpenDays()
    {
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedPeriodSummaryDto>
            {
                Day(1, daySealed: true),
                Day(2, daySealed: false, sealedWorks: 1),
                Day(3, daySealed: false),
                Day(4, daySealed: false, total: 0)
            });
        var skill = new GetPeriodStatusSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("1 of 4 day(s) sealed");
        result.Message.ShouldContain("NOT fully sealed");
        result.Message.ShouldContain("2026-06-02");
        result.Message.ShouldContain("2026-06-03");
    }

    [Test]
    public async Task ListPeriodIssues_GroupsBySeverity_AndAdvisesBeforeSealing()
    {
        _mediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto>
            {
                new() { Date = new DateOnly(2026, 6, 3), ClientName = "Anna Müller", Severity = ScheduleValidationType.Error, Code = "MISSING_WORKTIME" },
                new() { Date = new DateOnly(2026, 6, 5), ClientName = "Beat Keller", Severity = ScheduleValidationType.Warning, Code = "OVERTIME" }
            });
        var skill = new ListPeriodIssuesSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("2 validation finding(s)");
        result.Message.ShouldContain("before sealing");
    }

    [Test]
    public async Task ListPeriodIssues_ReportsCleanPeriod()
    {
        _mediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto>());
        var skill = new ListPeriodIssuesSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("clean");
    }

    [Test]
    public async Task ListPeriodAuditLog_ListsNewestFirst()
    {
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 7, 10));
        _mediator.Send(Arg.Any<GetPeriodAuditLogQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodAuditLogDto>
            {
                new() { Action = PeriodAuditAction.Seal, StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2026, 6, 30), GroupName = "Verkauf", AffectedCount = 42, PerformedAt = new DateTime(2026, 7, 1, 8, 0, 0) },
                new() { Action = PeriodAuditAction.Unseal, StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2026, 6, 30), Reason = "correction", AffectedCount = 42, PerformedAt = new DateTime(2026, 7, 2, 9, 0, 0) }
            });
        var skill = new ListPeriodAuditLogSkill(_mediator, clock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("2 seal/unseal audit entr");
    }

    [Test]
    public async Task ListOpenPeriods_ReportsSealStatePerPeriod()
    {
        _mediator.Send(Arg.Any<GetUsedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<UsedPeriodDto>
            {
                new() { StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2026, 6, 30), PaymentInterval = PaymentInterval.Monthly, GroupName = "Verkauf" },
                new() { StartDate = new DateOnly(2026, 5, 1), EndDate = new DateOnly(2026, 5, 31), PaymentInterval = PaymentInterval.Monthly, GroupName = "Verkauf" }
            });
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<SealedPeriodSummaryDto> { Day(1, false) },
                new List<SealedPeriodSummaryDto> { Day(1, true) });
        var skill = new ListOpenPeriodsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("2 populated billing period(s)");
        result.Message.ShouldContain("1 of the listed ones are fully");
    }
}
