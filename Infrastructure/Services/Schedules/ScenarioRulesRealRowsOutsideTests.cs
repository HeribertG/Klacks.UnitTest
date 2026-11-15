// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A rule check inside a scenario counts what the plan holds after an accept: the scenario's own rows plus the real works
/// and absences of the people that the scenario does not carry (another group, dates beyond the cloned span). A real work
/// on a shift the scenario carries is the scenario's business inside the cloned span and is never counted twice; the rows
/// of another scenario never count. Covers the evaluators that used to read the scenario token only: period cap, rest-day
/// rotation, counters, restricted time windows, planning rules, and the absences of the period-closing check.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ScenarioRulesRealRowsOutsideTests
{
    private static readonly DateOnly ScenarioFrom = new(2026, 7, 6);
    private static readonly DateOnly ScenarioUntil = new(2026, 7, 12);
    private static readonly DateOnly InsideSpan = new(2026, 7, 8);
    private static readonly DateOnly BeyondSpan = new(2026, 7, 28);
    private static readonly DateOnly Sunday1 = new(2026, 6, 21);
    private static readonly DateOnly Sunday2 = new(2026, 6, 28);
    private static readonly DateOnly Sunday3 = new(2026, 7, 5);
    private static readonly DateOnly Sunday4 = new(2026, 7, 12);
    private const decimal ShiftHours = 8m;

    private readonly Guid _token = Guid.NewGuid();
    private readonly Guid _otherToken = Guid.NewGuid();
    private readonly Guid _anna = Guid.NewGuid();
    private readonly Guid _berta = Guid.NewGuid();

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.AnalyseScenarios.Add(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = "Scenario",
            Token = _token,
            FromDate = ScenarioFrom,
            UntilDate = ScenarioUntil,
        });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task WorkHoursByDay_Scenario_CountsARealWorkOnAShiftTheScenarioDoesNotCarry()
    {
        SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedRealShift()), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await new ClientWorkHoursProvider(_context).GetWorkHoursByDayAsync(_anna, InsideSpan, InsideSpan, _token);

        hours[InsideSpan].ShouldBe(2 * ShiftHours);
    }

    [Test]
    public async Task WorkHoursByDay_Scenario_NeverCountsTheRealCopyOfAWorkThatLiesInTheScenario()
    {
        var realShift = SeedRealShift();
        var cloneShift = SeedCloneOf(realShift);
        SeedWork(_anna, realShift, InsideSpan, analyseToken: null);
        SeedWork(_anna, cloneShift, InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await new ClientWorkHoursProvider(_context).GetWorkHoursByDayAsync(_anna, InsideSpan, InsideSpan, _token);

        hours[InsideSpan].ShouldBe(ShiftHours, "the clone stands for the real work inside the cloned span");
    }

    [Test]
    public async Task WorkHoursByDay_Scenario_CountsARealWorkOnACarriedShiftBeyondTheClonedSpan()
    {
        var realShift = SeedRealShift();
        SeedCloneOf(realShift);
        SeedWork(_anna, realShift, BeyondSpan, analyseToken: null);
        await _context.SaveChangesAsync();

        var hours = await new ClientWorkHoursProvider(_context).GetWorkHoursByDayAsync(_anna, BeyondSpan, BeyondSpan, _token);

        hours[BeyondSpan].ShouldBe(ShiftHours, "no clone exists beyond the span, the real work stays in the plan");
    }

    [Test]
    public async Task WorkHoursByDay_Scenario_IgnoresTheWorkOfAnotherScenario()
    {
        SeedWork(_anna, SeedCloneOf(SeedRealShift(), _otherToken), InsideSpan, _otherToken);
        await _context.SaveChangesAsync();

        var hours = await new ClientWorkHoursProvider(_context).GetWorkHoursByDayAsync(_anna, InsideSpan, InsideSpan, _token);

        hours.ShouldBeEmpty();
    }

    [Test]
    public async Task WorkHoursByDay_RealPlan_StaysTheRealWorksOnly()
    {
        SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedRealShift()), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await new ClientWorkHoursProvider(_context).GetWorkHoursByDayAsync(_anna, InsideSpan, InsideSpan, analyseToken: null);

        hours[InsideSpan].ShouldBe(ShiftHours);
    }

    [Test]
    public async Task PeriodHours_Scenario_CountsARealWorkOnAShiftTheScenarioDoesNotCarry()
    {
        SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedRealShift()), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await HoursOfScenarioAsync();

        hours[_anna].ShouldBe(2 * ShiftHours);
    }

    [Test]
    public async Task PeriodHours_Scenario_NeverCountsTheRealCopyOfAWorkThatLiesInTheScenario()
    {
        var realShift = SeedRealShift();
        SeedWork(_anna, realShift, InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(realShift), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await HoursOfScenarioAsync();

        hours[_anna].ShouldBe(ShiftHours);
    }

    [Test]
    public async Task PeriodHours_Scenario_CountsARealAbsenceTheScenarioDoesNotCarry()
    {
        var absenceId = Guid.NewGuid();
        SeedBreak(_anna, absenceId, InsideSpan, analyseToken: null);
        await _context.SaveChangesAsync();

        var hours = await HoursOfScenarioAsync();

        hours[_anna].ShouldBe(ShiftHours);
    }

    [Test]
    public async Task PeriodHours_Scenario_CountsARealAbsenceOnceWhenTheScenarioCarriesItsTwin()
    {
        var absenceId = Guid.NewGuid();
        SeedBreak(_anna, absenceId, InsideSpan, analyseToken: null);
        SeedBreak(_anna, absenceId, InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await HoursOfScenarioAsync();

        hours[_anna].ShouldBe(ShiftHours);
    }

    [Test]
    public async Task PeriodHours_Scenario_CountsTheReplacementOfARealWorkOutsideTheScenario()
    {
        var work = SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work,
            ReplaceClientId = _berta,
            Type = WorkChangeType.ReplacementStart,
            ChangeTime = 3m,
        });
        await _context.SaveChangesAsync();

        var hours = await HoursOfScenarioAsync();

        hours[_berta].ShouldBe(3m);
        hours[_anna].ShouldBe(ShiftHours - 3m);
    }

    [Test]
    public async Task PeriodHours_Scenario_IgnoresTheWorkOfAnotherScenario()
    {
        SeedWork(_anna, SeedCloneOf(SeedRealShift(), _otherToken), InsideSpan, _otherToken);
        await _context.SaveChangesAsync();

        var hours = await HoursOfScenarioAsync();

        hours[_anna].ShouldBe(0m);
    }

    [Test]
    public async Task PeriodHours_RealPlan_StaysTheRealRowsOnly()
    {
        SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedRealShift()), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await NewPeriodHoursService().CalculateHoursForClientsAsync([_anna], ScenarioFrom, BeyondSpan, analyseToken: null);

        hours[_anna].ShouldBe(ShiftHours);
    }

    [Test]
    public async Task PeriodHoursOfOneClient_Scenario_StaysTheScenarioRowsOnly()
    {
        SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedRealShift()), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var hours = await NewPeriodHoursService().CalculatePeriodHoursAsync(_anna, ScenarioFrom, BeyondSpan, _token);

        hours.Hours.ShouldBe(ShiftHours, "the shared single-client read feeds the bulk and cache paths and stays token-only");
    }

    [Test]
    public async Task RestrictedTimeWindow_Scenario_SeesARealWorkOutsideTheScenario()
    {
        var evaluator = NewRestrictedTimeWindowEvaluator();
        SeedWork(_anna, SeedRealShift(), new DateOnly(2026, 7, 1), analyseToken: null, new TimeOnly(12, 0), new TimeOnly(16, 0));
        await _context.SaveChangesAsync();

        var entries = await evaluator.EvaluateAsync(_anna, "Anna", new DateOnly(2026, 7, 1), _token);

        entries.ShouldHaveSingleItem().Comment.ShouldBe(ScheduleValidationKeys.RestrictedTimeWindow);
    }

    [Test]
    public async Task RestrictedTimeWindow_Scenario_IgnoresTheWorkOfAnotherScenario()
    {
        var evaluator = NewRestrictedTimeWindowEvaluator();
        SeedWork(_anna, SeedCloneOf(SeedRealShift(), _otherToken), new DateOnly(2026, 7, 1), _otherToken, new TimeOnly(12, 0), new TimeOnly(16, 0));
        await _context.SaveChangesAsync();

        (await evaluator.EvaluateAsync(_anna, "Anna", new DateOnly(2026, 7, 1), _token)).ShouldBeEmpty();
    }

    [Test]
    public async Task RestDayRotation_Scenario_SeesTheRealWorksOutsideTheScenario()
    {
        var evaluator = NewRestDayRotationEvaluator();
        foreach (var sunday in new[] { Sunday1, Sunday2, Sunday3, Sunday4 })
        {
            SeedWork(_anna, SeedRealShift(), sunday, analyseToken: null, new TimeOnly(8, 0), new TimeOnly(16, 0));
        }

        await _context.SaveChangesAsync();

        var entries = await evaluator.EvaluateAsync(_anna, "Anna", Sunday4, _token);

        entries.ShouldHaveSingleItem().CommentParams["actualFree"].ShouldBe("0");
    }

    [Test]
    public async Task RestDayRotation_Scenario_IgnoresTheWorkOfAnotherScenario()
    {
        var evaluator = NewRestDayRotationEvaluator();
        foreach (var sunday in new[] { Sunday1, Sunday2, Sunday3, Sunday4 })
        {
            SeedWork(_anna, SeedCloneOf(SeedRealShift(), _otherToken), sunday, _otherToken, new TimeOnly(8, 0), new TimeOnly(16, 0));
        }

        await _context.SaveChangesAsync();

        (await evaluator.EvaluateAsync(_anna, "Anna", Sunday4, _token)).ShouldBeEmpty();
    }

    [Test]
    public async Task CounterRule_Scenario_SeesTheRealWorksOutsideTheScenario()
    {
        var evaluator = NewCounterRuleEvaluator();
        SeedWork(_anna, SeedRealShift(), new DateOnly(2026, 2, 3), analyseToken: null, new TimeOnly(22, 0), new TimeOnly(6, 0));
        SeedWork(_anna, SeedRealShift(), new DateOnly(2026, 4, 10), analyseToken: null, new TimeOnly(23, 30), new TimeOnly(7, 0));
        SeedWork(_anna, SeedRealShift(), new DateOnly(2026, 7, 11), analyseToken: null, new TimeOnly(22, 0), new TimeOnly(7, 0));
        await _context.SaveChangesAsync();

        var entries = await evaluator.EvaluateAsync(_anna, "Anna", new DateOnly(2026, 7, 13), _token);

        entries.ShouldHaveSingleItem().CommentParams["count"].ShouldBe("3");
    }

    [Test]
    public async Task CounterRule_Scenario_IgnoresTheWorkOfAnotherScenario()
    {
        var evaluator = NewCounterRuleEvaluator();
        SeedWork(_anna, SeedCloneOf(SeedRealShift(), _otherToken), new DateOnly(2026, 2, 3), _otherToken, new TimeOnly(22, 0), new TimeOnly(6, 0));
        await _context.SaveChangesAsync();

        (await evaluator.EvaluateAsync(_anna, "Anna", new DateOnly(2026, 7, 13), _token)).ShouldBeEmpty();
    }

    [Test]
    public async Task PlanningRuleWorkSpans_Scenario_SeeARealWorkOutsideTheScenario()
    {
        SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedRealShift()), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var spans = await new PlanningRuleDataReader(_context).GetWorkSpansAsync([_anna], InsideSpan, InsideSpan, _token);

        spans.Count.ShouldBe(2);
    }

    [Test]
    public async Task PlanningRuleWorkSpans_Scenario_NeverCountTheRealCopyOfAWorkThatLiesInTheScenario()
    {
        var realShift = SeedRealShift();
        SeedWork(_anna, realShift, InsideSpan, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(realShift), InsideSpan, _token);
        await _context.SaveChangesAsync();

        var spans = await new PlanningRuleDataReader(_context).GetWorkSpansAsync([_anna], InsideSpan, InsideSpan, _token);

        spans.ShouldHaveSingleItem();
    }

    [Test]
    public async Task SoftDeletedRows_AreNotCounted_InAScenarioRuleCheck()
    {
        SeedWork(_anna, SeedRealShift(), InsideSpan, analyseToken: null, isDeleted: true);
        SeedWork(_anna, SeedCloneOf(SeedRealShift()), InsideSpan, _token, isDeleted: true);
        SeedBreak(_anna, Guid.NewGuid(), InsideSpan, analyseToken: null, isDeleted: true);
        SeedBreak(_anna, Guid.NewGuid(), InsideSpan, _token, isDeleted: true);
        await _context.SaveChangesAsync();

        (await HoursOfScenarioAsync())[_anna].ShouldBe(0m);
        (await new ClientWorkHoursProvider(_context).GetWorkHoursByDayAsync(_anna, InsideSpan, InsideSpan, _token)).ShouldBeEmpty();
        (await OutsideScenarioWorkQuery.LoadAsync(_context, _token, [_anna], InsideSpan, InsideSpan, CancellationToken.None)).ShouldBeEmpty();
    }

    private async Task<Dictionary<Guid, decimal>> HoursOfScenarioAsync()
        => await NewPeriodHoursService().CalculateHoursForClientsAsync([_anna, _berta], ScenarioFrom, BeyondSpan, _token);

    private PeriodHoursService NewPeriodHoursService()
    {
        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        return new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contractDataProvider,
            Substitute.For<IWeekConfiguration>(),
            new FrozenPeriodPolicy(_context));
    }

    private RestrictedTimeWindowEvaluator NewRestrictedTimeWindowEvaluator()
    {
        var rules = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        rules.GetAllActiveAsync().Returns(new List<RestrictedTimeWindowRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SeasonFromMonth = 6,
                SeasonFromDay = 15,
                SeasonToMonth = 9,
                SeasonToDay = 15,
                DailyStart = new TimeOnly(12, 30),
                DailyEnd = new TimeOnly(15, 0),
                AppliesToGroupTag = string.Empty,
            },
        });
        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(ComplianceRuleNames.RestrictedTimeWindow).Returns(RuleEnforcementMode.Warn);
        return new RestrictedTimeWindowEvaluator(rules, _context, enforcement);
    }

    private RestDayRotationEvaluator NewRestDayRotationEvaluator()
    {
        var rules = Substitute.For<IRestDayRotationRuleRepository>();
        rules.GetAllActiveAsync().Returns(new List<RestDayRotationRule>
        {
            new() { Id = Guid.NewGuid(), DayOfWeek = DayOfWeek.Sunday, MinFreeCount = 2, WindowWeeks = 4 },
        });
        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(ComplianceRuleNames.RestDayRotation).Returns(RuleEnforcementMode.Warn);
        var membership = Substitute.For<IClientMembershipStartResolver>();
        membership.GetValidFromAsync(Arg.Any<Guid>()).Returns((DateOnly?)null);
        var contract = Substitute.For<IClientContractDataProvider>();
        contract.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { MinRestDays = 2m });
        return new RestDayRotationEvaluator(rules, _context, enforcement, membership, contract);
    }

    private CounterRuleEvaluator NewCounterRuleEvaluator()
    {
        var rules = Substitute.For<ICounterRuleRepository>();
        rules.GetAllActiveAsync().Returns(new List<CounterRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                EventType = CounterEventType.NightShift,
                Period = CounterPeriod.Year,
                Threshold = 3,
            },
        });
        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Warn);
        var contract = Substitute.For<IClientContractDataProvider>();
        contract.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        return new CounterRuleEvaluator(rules, _context, enforcement, contract);
    }

    private Guid SeedRealShift()
    {
        var id = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = id, Name = "Real", Status = ShiftStatus.SplitShift });
        return id;
    }

    private Guid SeedCloneOf(Guid realShiftId, Guid? token = null)
    {
        var id = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = id,
            Name = "Clone",
            Status = ShiftStatus.SplitShift,
            AnalyseToken = token ?? _token,
            ScenarioSourceShiftId = realShiftId,
        });
        return id;
    }

    private Guid SeedWork(
        Guid clientId, Guid shiftId, DateOnly date, Guid? analyseToken, TimeOnly? start = null, TimeOnly? end = null, bool isDeleted = false)
    {
        var id = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = start ?? new TimeOnly(8, 0),
            EndTime = end ?? new TimeOnly(16, 0),
            WorkTime = ShiftHours,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
        });
        return id;
    }

    private void SeedBreak(Guid clientId, Guid absenceId, DateOnly date, Guid? analyseToken, bool isDeleted = false)
        => _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = ShiftHours,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
        });
}
