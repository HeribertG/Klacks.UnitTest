// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parity between the automatic close and the advice for a person: the real PeriodAutoCloseService (armed, allowed, lag
/// stored, inside the window) and the real evaluate_period_close_readiness run over the SAME fakes, and the verdicts
/// are compared. Scope: the gates both paths share (candidate period, last day sealed, no own work, errors, clean).
/// The gates that bind only Klacksy are out of scope on purpose, because there the paths differ by design: autonomy,
/// window, tick budget, and the reopen and partial-seal stops, which leave the decision to a person.
/// Default scenario: company day 2026-09-02, monthly group, stored lag 1, so the August period is due.
/// </summary>

using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseReadinessParityTests
{
    private static readonly Guid DecidingAdminId = Guid.Parse("3f1c9a52-0000-0000-0000-000000000002");
    private static readonly DateOnly Today = new(2026, 9, 2);
    private static readonly DateOnly AugustStart = new(2026, 8, 1);
    private static readonly DateOnly AugustEnd = new(2026, 8, 31);

    private Group _group = null!;
    private IGroupRepository _groups = null!;
    private IWeekConfiguration _week = null!;
    private ISettingsReader _settings = null!;
    private IPeriodAutoCloseResolver _resolver = null!;
    private ISealedDayRepository _sealedDays = null!;
    private IScheduleActivityProbe _probe = null!;
    private IPeriodAuditLogRepository _auditLog = null!;
    private IWorkRepository _works = null!;
    private IMediator _mediator = null!;
    private IServiceScopeFactory _scopeFactory = null!;
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
        _groups = Substitute.For<IGroupRepository>();
        _groups.List().Returns(new List<Group> { _group });
        _groups.Get(_group.Id).Returns(_group);
        _groups.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>()).Returns(new List<Guid> { _group.Id });
        _week = Substitute.For<IWeekConfiguration>();
        _week.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<DateOnly>());
        _settings = Substitute.For<ISettingsReader>();
        StubLag("1");
        _resolver = Substitute.For<IPeriodAutoCloseResolver>();
        _resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodAutoCloseDecision(AutonomyLevel.FullyAutonomous, DecidingAdminId, true, PeriodAutoCloseBlockedBy.None));
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

        _mediator = Substitute.For<IMediator>();
        StubIssues();
        _mediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<ClosePeriodByGroupCommand>();
                for (var day = command.StartDate; day <= command.EndDate; day = day.AddDays(1))
                {
                    _sealed.Add(day);
                }

                return 31;
            });

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPeriodAutoCloseResolver)).Returns(_resolver);
        provider.GetService(typeof(ISealedDayRepository)).Returns(_sealedDays);
        provider.GetService(typeof(IMediator)).Returns(_mediator);
        provider.GetService(typeof(IPeriodAuditLogRepository)).Returns(_auditLog);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);
    }

    private void StubLag(string value) =>
        _settings.GetSetting(SettingKeys.PeriodCloseLagDays).Returns(Task.FromResult<SettingsRow?>(
            new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = value }));

    private void StubIssues(params ScheduleValidationType[] severities) =>
        _mediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>())
            .Returns(severities.Select(severity => new PeriodIssueDto { Severity = severity }).ToList());

    private static FixedCompanyClock Clock() =>
        new(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));

    private async Task<PeriodCloseReadinessEvaluation> AdviseAsync()
    {
        var skill = new EvaluatePeriodCloseReadinessSkill(
            _mediator, _groups, TestGroupScopeGuard.Unrestricted(), _week, _settings, Clock(),
            _sealedDays, _probe, _auditLog, _works);
        var context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new List<string> { Roles.Admin }
        };

        var result = await skill.ExecuteAsync(context, new Dictionary<string, object> { ["groupId"] = _group.Id.ToString() });
        return result.Data.ShouldBeOfType<PeriodCloseReadinessEvaluation>();
    }

    private Task<IReadOnlyList<IAgentTriggerEvent>> RunAutoCloseAsync() =>
        new PeriodAutoCloseService(
            _groups, _week, _settings, Clock(), _resolver, _sealedDays, _probe, _auditLog, _scopeFactory,
            NullLogger<PeriodAutoCloseService>.Instance).RunAsync();

    [Test]
    public async Task CleanDuePeriod_AutoCloseSeals_AdviceSaysCloseNow_ForTheSamePeriod()
    {
        var advice = await AdviseAsync();

        var events = await RunAutoCloseAsync();

        advice.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        var closed = events.Single().ShouldBeOfType<PeriodAutoClosedTriggerEvent>();
        closed.PeriodStartDate.ShouldBe(advice.PeriodStart);
        closed.PeriodEndDate.ShouldBe(advice.PeriodEnd);
    }

    [Test]
    public async Task OpenErrors_BothStop_WithTheSameCount()
    {
        StubIssues(ScheduleValidationType.Error, ScheduleValidationType.Error, ScheduleValidationType.Warning, ScheduleValidationType.Info);

        var advice = await AdviseAsync();
        var events = await RunAutoCloseAsync();

        advice.Verdict.ShouldBe(PeriodCloseVerdict.FixErrorsFirst);
        advice.ErrorCount.ShouldBe(2);
        var blocked = events.Single().ShouldBeOfType<PeriodAutoCloseBlockedTriggerEvent>();
        blocked.Reason.ShouldBe(PeriodAutoCloseBlockReason.OpenErrors);
        blocked.ErrorCount.ShouldBe(advice.ErrorCount);
        await _mediator.DidNotReceive().Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WarningsOnly_NeitherPathStops()
    {
        StubIssues(ScheduleValidationType.Warning);

        var advice = await AdviseAsync();
        var events = await RunAutoCloseAsync();

        advice.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        events.Single().ShouldBeOfType<PeriodAutoClosedTriggerEvent>();
    }

    [Test]
    public async Task LastDaySealed_AutoCloseIsSilent_AdviceSaysNothingToSeal()
    {
        _sealed.Add(AugustEnd);

        var advice = await AdviseAsync();
        var events = await RunAutoCloseAsync();

        advice.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        advice.PeriodEnd.ShouldBe(AugustEnd);
        events.ShouldBeEmpty();
    }

    [Test]
    public async Task NoOwnWork_AutoCloseIsSilent_AdviceSaysNothingToSeal()
    {
        _probe.HasDirectWorkInRangeAsync(_group, AugustStart, AugustEnd, Arg.Any<CancellationToken>()).Returns(false);

        var advice = await AdviseAsync();
        var events = await RunAutoCloseAsync();

        advice.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        events.ShouldBeEmpty();
    }

    [Test]
    public async Task LongLag_BothPickTheSameDuePeriod_WhichIsLeftToAPersonOutsideTheWindow()
    {
        StubLag("5");

        var advice = await AdviseAsync();
        var events = await RunAutoCloseAsync();

        var blocked = events.Single().ShouldBeOfType<PeriodAutoCloseBlockedTriggerEvent>();
        blocked.Reason.ShouldBe(PeriodAutoCloseBlockReason.CloseWindowMissed);
        advice.PeriodEnd.ShouldBe(new DateOnly(2026, 7, 31));
        blocked.PeriodEndDate.ShouldBe(advice.PeriodEnd);
        advice.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
    }
}
