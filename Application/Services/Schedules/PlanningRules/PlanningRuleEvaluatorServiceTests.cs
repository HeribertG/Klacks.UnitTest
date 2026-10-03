// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlanningRuleEvaluatorService against a stubbed loader and an in-memory Work list: mapping of hard
/// and soft findings, team findings without client, the day filter, the CounterRule exclusion, and the pre-commit
/// before/after comparison (untouched violations, worsened runs, anchor shifts, removals that create a finding,
/// and the horizon widening that RestAfterKind needs).
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Application.Services.Schedules.PlanningRules;

[TestFixture]
public class PlanningRuleEvaluatorServiceTests
{
    private static readonly DateOnly Monday = new(2026, 7, 13);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);
    private static readonly TimeOnly EarlyStart = new(6, 0);
    private static readonly TimeOnly EarlyEnd = new(14, 0);
    private static readonly CoreNightWindow NightWindow = new(new TimeOnly(23, 0), new TimeOnly(6, 0));

    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();

    private IPlanningRuleSetLoader _loader = null!;
    private IPlanningRuleDataReader _reader = null!;
    private List<PlanningRuleWorkSpan> _works = null!;
    private List<PlanRule> _rules = null!;
    private PlanningRuleEvaluatorService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _works = [];
        _rules = [];
        _loader = Substitute.For<IPlanningRuleSetLoader>();
        _loader
            .LoadRuleSetAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<PlanningRuleSources>(), Arg.Any<CancellationToken>())
            .Returns(call => new PlanningRuleSet(
                _rules,
                call.ArgAt<IReadOnlyCollection<Guid>>(0).Distinct().Select(id => new RuleAgent(id.ToString(), NightWindow, 100m)).ToList(),
                [],
                []));
        _reader = Substitute.For<IPlanningRuleDataReader>();
        _reader
            .GetWorkSpansAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var clients = call.ArgAt<IReadOnlyCollection<Guid>>(0);
                var from = call.ArgAt<DateOnly>(1);
                var until = call.ArgAt<DateOnly>(2);
                return _works.Where(w => clients.Contains(w.ClientId) && w.Date >= from && w.Date <= until).ToList();
            });
        _sut = new PlanningRuleEvaluatorService(_loader, _reader);
    }

    [Test]
    public async Task EvaluateRangeAsync_LoadsOnlyPlanningConstraints()
    {
        await _sut.EvaluateRangeAsync([_clientA], Monday, Monday.AddDays(6), null, new Dictionary<Guid, string>());

        await _loader.Received(1).LoadRuleSetAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Monday, Monday.AddDays(6), null,
            Arg.Any<int>(), PlanningRuleSources.PlanningConstraints, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateRangeAsync_NoRules_DoesNotReadWorks()
    {
        (await _sut.EvaluateRangeAsync([_clientA], Monday, Monday.AddDays(6), null, new Dictionary<Guid, string>())).ShouldBeEmpty();

        await _reader.DidNotReceiveWithAnyArgs().GetWorkSpansAsync(default!, default, default, default, default);
    }

    [Test]
    public async Task EvaluateRangeAsync_HardRunViolation_IsOverridableErrorWithPlanningRuleKey()
    {
        var ruleId = Guid.NewGuid();
        _rules.Add(new MaxConsecutiveOfKindRule(ruleId, RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2));
        SeedNights(_clientA, Monday, 3);

        var entry = (await _sut.EvaluateRangeAsync(
            [_clientA], Monday, Monday.AddDays(6), null, new Dictionary<Guid, string> { [_clientA] = "Anna" })).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.ClientId.ShouldBe(_clientA);
        entry.ClientName.ShouldBe("Anna");
        entry.Date.ShouldBe(Monday);
        entry.Comment.ShouldBe(ScheduleValidationKeys.PlanningRule);
        entry.CommentParams[PlanningRuleNotificationMapper.KindParam].ShouldBe(nameof(PlanRuleKind.MaxConsecutiveOfKind));
        entry.CommentParams[PlanningRuleNotificationMapper.ObservedParam].ShouldBe("3");
        entry.CommentParams[PlanningRuleNotificationMapper.LimitParam].ShouldBe("2");
        entry.CommentParams[PlanningRuleNotificationMapper.RuleIdParam].ShouldBe(ruleId.ToString());
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.PlanningRule);
    }

    [Test]
    public async Task EvaluateRangeAsync_SoftRunViolation_IsUntaggedWarning()
    {
        _rules.Add(new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Soft, 1d, RuleShiftKind.Night, 2));
        SeedNights(_clientA, Monday, 3);

        var entry = (await _sut.EvaluateRangeAsync([_clientA], Monday, Monday.AddDays(6), null, new Dictionary<Guid, string>())).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.CommentParams.ContainsKey(ComplianceRuleNames.EnforcementRuleParamKey).ShouldBeFalse();
    }

    [Test]
    public async Task EvaluateRangeAsync_TeamFairnessSpread_IsReportedWithoutClient()
    {
        _rules.Add(new TeamFairnessRule(Guid.NewGuid(), 1d, FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, false, new HashSet<DayOfWeek>()));
        SeedNights(_clientA, Monday, 3);

        var entry = (await _sut.EvaluateRangeAsync([_clientA, _clientB], Monday, Monday.AddDays(6), null, new Dictionary<Guid, string>()))
            .ShouldHaveSingleItem();

        entry.ClientId.ShouldBe(Guid.Empty);
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.CommentParams[PlanningRuleNotificationMapper.KindParam].ShouldBe(nameof(PlanRuleKind.TeamFairness));
    }

    [Test]
    public async Task EvaluateDayAsync_ReportsOnlyFindingsDatedOnTheDay_AndNeverTeamFairness()
    {
        _rules.Add(new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2));
        _rules.Add(new TeamFairnessRule(Guid.NewGuid(), 1d, FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 0m, false, new HashSet<DayOfWeek>()));
        SeedNights(_clientA, Monday, 3);

        (await _sut.EvaluateDayAsync(_clientA, "Anna", Monday.AddDays(1), null)).ShouldBeEmpty();
        var entry = (await _sut.EvaluateDayAsync(_clientA, "Anna", Monday, null)).ShouldHaveSingleItem();
        entry.CommentParams[PlanningRuleNotificationMapper.KindParam].ShouldBe(nameof(PlanRuleKind.MaxConsecutiveOfKind));
    }

    [Test]
    public async Task EvaluatePlannedChangeAsync_UntouchedExistingViolation_IsNotReported()
    {
        _rules.Add(new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2));
        SeedNights(_clientA, Monday, 4);

        var result = await _sut.EvaluatePlannedChangeAsync([Early(_clientA, Monday.AddDays(5))], [], null);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluatePlannedChangeAsync_DayAddedInFrontOfViolatingRun_IsReported()
    {
        _rules.Add(new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2));
        SeedNights(_clientA, Monday, 3);

        var entry = (await _sut.EvaluatePlannedChangeAsync([Night(_clientA, Monday.AddDays(-1))], [], null)).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.Date.ShouldBe(Monday.AddDays(-1));
        entry.CommentParams[PlanningRuleNotificationMapper.ObservedParam].ShouldBe("4");
    }

    [Test]
    public async Task EvaluatePlannedChangeAsync_RemovingTheFirstDayOfARun_IsNotReported()
    {
        _rules.Add(new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2));
        SeedNights(_clientA, Monday, 4);
        var first = _works[0];

        var result = await _sut.EvaluatePlannedChangeAsync(
            [], [new PlannedRemovalRow(_clientA, first.Date, first.StartTime, first.EndTime, first.WorkId)], null);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluatePlannedChangeAsync_RemovalSplittingABlock_ReportsTheNewRestFinding()
    {
        _rules.Add(new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 2));
        SeedNights(_clientA, Monday, 4);
        var second = _works[1];

        var entry = (await _sut.EvaluatePlannedChangeAsync(
            [], [new PlannedRemovalRow(_clientA, second.Date, second.StartTime, second.EndTime)], null)).ShouldHaveSingleItem();

        entry.Date.ShouldBe(Monday);
        entry.CommentParams[PlanningRuleNotificationMapper.KindParam].ShouldBe(nameof(PlanRuleKind.RestAfterKind));
    }

    [Test]
    public async Task EvaluatePlannedChangeAsync_NightBeforeExistingWork_IsCaughtThroughTheWidenedWindow()
    {
        _rules.Add(new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 1));
        _works.Add(Span(_clientA, Monday.AddDays(1), EarlyStart, EarlyEnd));

        var entry = (await _sut.EvaluatePlannedChangeAsync([Night(_clientA, Monday)], [], null)).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.ClientId.ShouldBe(_clientA);
        entry.Date.ShouldBe(Monday);
    }

    [Test]
    public async Task EvaluatePlannedChangeAsync_NewSoftFinding_IsWarning()
    {
        _rules.Add(new ForbiddenTransitionRule(Guid.NewGuid(), RuleSeverity.Soft, 1d, RuleShiftKind.Night, RuleShiftKind.Early, 1));
        _works.Add(Span(_clientA, Monday.AddDays(1), EarlyStart, EarlyEnd));

        var entry = (await _sut.EvaluatePlannedChangeAsync([Night(_clientA, Monday)], [], null)).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.ClientName.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluatePlannedChangeAsync_TeamFairnessIsLeftOut()
    {
        _rules.Add(new TeamFairnessRule(Guid.NewGuid(), 1d, FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 0m, false, new HashSet<DayOfWeek>()));

        (await _sut.EvaluatePlannedChangeAsync([Night(_clientA, Monday)], [], null)).ShouldBeEmpty();
        await _reader.DidNotReceiveWithAnyArgs().GetWorkSpansAsync(default!, default, default, default, default);
    }

    private void SeedNights(Guid clientId, DateOnly first, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _works.Add(Span(clientId, first.AddDays(i), NightStart, NightEnd));
        }
    }

    private static PlanningRuleWorkSpan Span(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end)
        => new(clientId, date, start, end, 8m, Guid.NewGuid());

    private static PlannedWorkRow Night(Guid clientId, DateOnly date) => new(clientId, date, NightStart, NightEnd);

    private static PlannedWorkRow Early(Guid clientId, DateOnly date) => new(clientId, date, EarlyStart, EarlyEnd);
}
