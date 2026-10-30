// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for evaluate_period_close_readiness: group and date input, which period is chosen, which facts reach the
/// advisor and that the skill never sends a close. Default scenario: company day 2026-09-02, monthly group "Bern",
/// stored lag 1, so the August period is due.
/// </summary>

using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluatePeriodCloseReadinessSkillTests
{
    private static readonly DateOnly Today = new(2026, 9, 2);
    private static readonly DateOnly AugustStart = new(2026, 8, 1);
    private static readonly DateOnly AugustEnd = new(2026, 8, 31);
    private static readonly DateOnly JulyStart = new(2026, 7, 1);
    private static readonly DateOnly JulyEnd = new(2026, 7, 31);

    private Group _group = null!;
    private IMediator _mediator = null!;
    private IGroupRepository _groups = null!;
    private IWeekConfiguration _week = null!;
    private ISettingsReader _settings = null!;
    private ISealedDayRepository _sealedDays = null!;
    private IScheduleActivityProbe _probe = null!;
    private IPeriodAuditLogRepository _auditLog = null!;
    private IWorkRepository _works = null!;
    private HashSet<DateOnly> _sealed = null!;

    [SetUp]
    public void Setup()
    {
        _sealed = [];
        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Bern",
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>()).Returns(new List<PeriodIssueDto>());
        _groups = Substitute.For<IGroupRepository>();
        _groups.List().Returns(new List<Group> { _group });
        _groups.Get(_group.Id).Returns(_group);
        _groups.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>()).Returns(new List<Guid> { _group.Id });
        _week = Substitute.For<IWeekConfiguration>();
        _settings = Substitute.For<ISettingsReader>();
        StubLag("1");
        _sealedDays = Substitute.For<ISealedDayRepository>();
        _sealedDays.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => _sealed
                .Where(day => day >= call.ArgAt<DateOnly>(0) && day <= call.ArgAt<DateOnly>(1))
                .Select(day => new SealedDay { Date = day })
                .ToList());
        _probe = Substitute.For<IScheduleActivityProbe>();
        _probe.HasDirectWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _auditLog = Substitute.For<IPeriodAuditLogRepository>();
        _auditLog.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodAuditLog>());
        _works = Substitute.For<IWorkRepository>();
        _works.GetSealingSummaryAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(DateOnly Date, int Total, int Sealed)>());
    }

    private void StubLag(string value) =>
        _settings.GetSetting(SettingKeys.PeriodCloseLagDays).Returns(Task.FromResult<SettingsRow?>(
            new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = value }));

    private EvaluatePeriodCloseReadinessSkill Skill() => new(
        _mediator,
        _groups,
        TestGroupScopeGuard.Unrestricted(),
        _week,
        _settings,
        new FixedCompanyClock(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))),
        _sealedDays,
        _probe,
        _auditLog,
        _works);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    private Dictionary<string, object> ByGroupId() => new() { ["groupId"] = _group.Id.ToString() };

    private async Task<PeriodCloseReadinessEvaluation> EvaluateAsync(Dictionary<string, object>? parameters = null)
    {
        var result = await Skill().ExecuteAsync(Ctx(), parameters ?? ByGroupId());
        result.Success.ShouldBeTrue(result.Message);
        return result.Data.ShouldBeOfType<PeriodCloseReadinessEvaluation>();
    }

    private void Seal(DateOnly from, DateOnly until)
    {
        for (var day = from; day <= until; day = day.AddDays(1))
        {
            _sealed.Add(day);
        }
    }

    [Test]
    public async Task WithoutAGroup_IsRefused_BecauseReadinessExistsPerGroup()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("Name the group");
        await _mediator.DidNotReceive().Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyOneDate_IsRefused()
    {
        var parameters = ByGroupId();
        parameters["startDate"] = "2026-08-01";

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("together");
    }

    [Test]
    public async Task ReversedDates_AreRefused()
    {
        var parameters = ByGroupId();
        parameters["startDate"] = "2026-08-31";
        parameters["endDate"] = "2026-08-01";

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("on or before");
    }

    [Test]
    public async Task AFuzzyGroupName_IsNotGuessed_BecauseTheCloseWouldNotGuessEither()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupName"] = "Bernn" });

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IndividualGroup_WithoutDates_AsksForDates()
    {
        _group.PaymentInterval = PaymentInterval.Individual;

        var result = await Skill().ExecuteAsync(Ctx(), ByGroupId());

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("pass startDate and endDate");
    }

    [Test]
    public async Task WithoutDates_ChoosesTheDuePeriod_AndCountsItsFindingsForThatGroup()
    {
        _mediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>()).Returns(new List<PeriodIssueDto>
        {
            new() { Severity = ScheduleValidationType.Error },
            new() { Severity = ScheduleValidationType.Warning },
            new() { Severity = ScheduleValidationType.Warning },
            new() { Severity = ScheduleValidationType.Info }
        });

        var evaluation = await EvaluateAsync();

        evaluation.PeriodStart.ShouldBe(AugustStart);
        evaluation.PeriodEnd.ShouldBe(AugustEnd);
        evaluation.ErrorCount.ShouldBe(1);
        evaluation.WarningCount.ShouldBe(2);
        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.FixErrorsFirst);
        await _mediator.Received(1).Send(
            Arg.Is<GetPeriodIssuesQuery>(q => q.From == AugustStart && q.To == AugustEnd && q.GroupId == _group.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DuePeriodSealed_MovesOnToTheEndedPeriod_WhichMustWait()
    {
        StubLag("5");
        Seal(JulyStart, JulyEnd);

        var evaluation = await EvaluateAsync();

        evaluation.PeriodEnd.ShouldBe(AugustEnd);
        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
        evaluation.EarliestCloseDate.ShouldBe(new DateOnly(2026, 9, 5));
    }

    [Test]
    public async Task DuePeriodOpen_StaysTheChoice_EvenWhenALaterPeriodHasEnded()
    {
        StubLag("5");

        var evaluation = await EvaluateAsync();

        evaluation.PeriodEnd.ShouldBe(JulyEnd);
        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
    }

    [Test]
    public async Task ExplicitDates_AreEvaluatedAsGiven()
    {
        var parameters = ByGroupId();
        parameters["startDate"] = "2026-09-01";
        parameters["endDate"] = "2026-09-30";

        var evaluation = await EvaluateAsync(parameters);

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
        evaluation.EarliestCloseDate.ShouldBe(new DateOnly(2026, 10, 1));
    }

    [Test]
    public async Task ParentGroup_WithWorkOnlyOnChildren_ReportsTheTrap_AndSkipsTheFindingLoad()
    {
        _probe.HasDirectWorkInRangeAsync(_group, AugustStart, AugustEnd, Arg.Any<CancellationToken>()).Returns(false);
        _probe.HasWorkInRangeAsync(_group, AugustStart, AugustEnd, Arg.Any<CancellationToken>()).Returns(true);

        var evaluation = await EvaluateAsync();

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        evaluation.Findings.Single().Code.ShouldBe(PeriodCloseFindingCode.WorkOnlyOnChildGroups);
        await _mediator.DidNotReceive().Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReopenedPeriod_IsStillChecked_ForAPerson()
    {
        _auditLog.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodAuditLog>
            {
                PeriodAuditLog.For(PeriodAuditAction.Unseal, AugustStart, AugustEnd, _group.Id, "fix", 3, "admin", "Ada")
            });

        var evaluation = await EvaluateAsync();

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        evaluation.Findings.Single().Code.ShouldBe(PeriodCloseFindingCode.ReopenedBefore);
        await _mediator.Received(1).Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnsealedDays_AreDaysWithWorkWithoutADayLock_InDateOrder()
    {
        var sealedDay = AugustStart.AddDays(2);
        Seal(sealedDay, sealedDay);
        _works.GetSealingSummaryAsync(AugustStart, AugustEnd, _group.Id, Arg.Any<CancellationToken>())
            .Returns(new List<(DateOnly Date, int Total, int Sealed)>
            {
                (AugustStart.AddDays(5), 2, 0),
                (sealedDay, 3, 3),
                (AugustStart, 1, 0),
                (AugustStart.AddDays(6), 0, 0)
            });

        var evaluation = await EvaluateAsync();

        evaluation.UnsealedDays.ShouldBe([AugustStart, AugustStart.AddDays(5)]);
        evaluation.SealedDayCount.ShouldBe(1);
        evaluation.Findings.Select(f => f.Code).ShouldContain(PeriodCloseFindingCode.PartiallySealed);
    }

    [Test]
    public async Task TheSkill_NeverSendsAClose()
    {
        await EvaluateAsync();

        await _mediator.DidNotReceive().Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>());
    }
}
