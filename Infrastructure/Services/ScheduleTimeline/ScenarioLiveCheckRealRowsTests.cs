// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The live checks of a scenario (single day, client timeline, range, holiday spill-in) read the rows the rule checks read
/// (ScenarioRuleRows): the scenario's own rows plus the real rows of its people that the scenario does not carry. A real
/// work on a carried shift inside the cloned span is the scenario's business and never counts twice; without a token only
/// the real plan counts. The range and spill-in loads add real rows only for people holding a scenario row in the range.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.ScheduleTimeline;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScenarioLiveCheckRealRowsTests
{
    private static readonly DateOnly ScenarioFrom = new(2026, 7, 6);
    private static readonly DateOnly ScenarioUntil = new(2026, 7, 12);
    private static readonly DateOnly Day = new(2026, 7, 8);
    private static readonly DateOnly DayBefore = Day.AddDays(-1);
    private static readonly TimeOnly EarlyStart = new(6, 0);
    private static readonly TimeOnly EarlyEnd = new(10, 0);
    private static readonly TimeOnly LateStart = new(14, 0);
    private static readonly TimeOnly LateEnd = new(18, 0);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);

    private readonly Guid _token = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private TimelineCalculationService _calculation = null!;
    private Guid _anna;
    private Guid _carl;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), builder => builder.EnableNullChecks(false))
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _calculation = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()), NullLogger<TimelineCalculationService>.Instance);
        _context.AnalyseScenarios.Add(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = "Scenario",
            Token = _token,
            FromDate = ScenarioFrom,
            UntilDate = ScenarioUntil,
        });
        _anna = SeedClient("Anna");
        _carl = SeedClient("Carl");
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task LoadDay_Scenario_SeesTheRealWorkOfThePersonOnAShiftTheScenarioDoesNotCarry()
    {
        var realWork = SeedWork(_anna, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedShift()), Day, EarlyStart, EarlyEnd, _token);
        await _context.SaveChangesAsync();

        var rows = await NewLoader().LoadDayAsync(_anna, Day, _token, CancellationToken.None);

        rows.OwnWorks.Count.ShouldBe(2);
        rows.OwnWorks.ShouldContain(w => w.Id == realWork, "the duty in the other group stays in the plan after an accept");
    }

    [Test]
    public async Task LoadDay_Scenario_NeverSeesTheRealCopyOfACarriedWorkInsideTheSpan()
    {
        var realShift = SeedShift();
        SeedWork(_anna, realShift, Day, EarlyStart, EarlyEnd, analyseToken: null);
        var clone = SeedWork(_anna, SeedCloneOf(realShift), Day, EarlyStart, EarlyEnd, _token);
        await _context.SaveChangesAsync();

        var rows = await NewLoader().LoadDayAsync(_anna, Day, _token, CancellationToken.None);

        rows.OwnWorks.ShouldHaveSingleItem().Id.ShouldBe(clone);
    }

    [Test]
    public async Task LoadDay_RealPlan_ReadsTheRealRowsOnly()
    {
        var realWork = SeedWork(_anna, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedShift()), Day, EarlyStart, EarlyEnd, _token);
        SeedAbsence(_anna, Day, _token);
        await _context.SaveChangesAsync();

        var rows = await NewLoader().LoadDayAsync(_anna, Day, analyseToken: null, CancellationToken.None);

        rows.OwnWorks.ShouldHaveSingleItem().Id.ShouldBe(realWork);
        rows.Breaks.ShouldBeEmpty();
    }

    [Test]
    public async Task ClientTimeline_Scenario_SeesTheRealWorkAndTheRealAbsenceOutsideTheScenario()
    {
        SeedWork(_anna, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedShift()), Day, EarlyStart, EarlyEnd, _token);
        SeedAbsence(_anna, Day.AddDays(1), analyseToken: null);
        await _context.SaveChangesAsync();

        var timeline = await ClientTimelineLoader.LoadAsync(
            _context, _calculation, _anna, Day, Day.AddDays(1), _token, CancellationToken.None);

        timeline.Blocks.Count(b => b.OwnerDate == Day).ShouldBe(2, "the scenario duty and the real duty in the other group");
        timeline.Blocks.Count(b => b.OwnerDate == Day.AddDays(1)).ShouldBe(1, "the real absence the scenario holds no twin of");
    }

    [Test]
    public async Task ClientTimeline_RealPlan_ReadsTheRealRowsOnly()
    {
        SeedWork(_anna, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedShift()), Day, EarlyStart, EarlyEnd, _token);
        await _context.SaveChangesAsync();

        var timeline = await ClientTimelineLoader.LoadAsync(_context, _calculation, _anna, Day, Day, null, CancellationToken.None);

        timeline.Blocks.ShouldHaveSingleItem();
    }

    [Test]
    public async Task LoadRange_Scenario_AddsTheRealRowsOfThePeopleOfTheScenarioOnly()
    {
        var annasRealWork = SeedWork(_anna, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedShift()), Day, EarlyStart, EarlyEnd, _token);
        SeedWork(_carl, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        SeedAbsence(_carl, Day.AddDays(1), analyseToken: null);
        await _context.SaveChangesAsync();

        var rows = await NewLoader().LoadRangeAsync(ScenarioFrom, ScenarioUntil, _token, CancellationToken.None);

        rows.Works.Count(w => w.ClientId == _anna).ShouldBe(2);
        rows.Works.ShouldContain(w => w.Id == annasRealWork);
        rows.Works.ShouldNotContain(w => w.ClientId == _carl, "Carl holds no row in the scenario");
        rows.Breaks.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadRange_RealPlan_ReadsTheRealRowsOnly()
    {
        SeedWork(_anna, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        SeedWork(_anna, SeedCloneOf(SeedShift()), Day, EarlyStart, EarlyEnd, _token);
        SeedWork(_carl, SeedShift(), Day, LateStart, LateEnd, analyseToken: null);
        await _context.SaveChangesAsync();

        var rows = await NewLoader().LoadRangeAsync(ScenarioFrom, ScenarioUntil, null, CancellationToken.None);

        rows.Works.Count.ShouldBe(2);
        rows.Works.ShouldAllBe(w => w.AnalyseToken == null);
    }

    [Test]
    public async Task SpillIn_Scenario_ReportsTheRealNightShiftOfAPersonOfTheRangeOnly()
    {
        SeedWork(_anna, SeedShift(), DayBefore, NightStart, NightEnd, analyseToken: null);
        SeedWork(_carl, SeedShift(), DayBefore, NightStart, NightEnd, analyseToken: null);
        await _context.SaveChangesAsync();

        var spillIn = await HolidayWorkSpillInLoader.LoadAsync(
            _context, _calculation, Day, _token, [_anna], CancellationToken.None);

        spillIn.DatesByClient.Keys.ShouldBe([_anna], "Carl holds no row in the checked range of the scenario");
        spillIn.DatesByClient[_anna].ShouldBe([Day]);
    }

    [Test]
    public async Task SpillIn_RealPlan_IgnoresTheScenarioScope()
    {
        SeedWork(_anna, SeedShift(), DayBefore, NightStart, NightEnd, analyseToken: null);
        SeedWork(_carl, SeedShift(), DayBefore, NightStart, NightEnd, analyseToken: null);
        await _context.SaveChangesAsync();

        var spillIn = await HolidayWorkSpillInLoader.LoadAsync(
            _context, _calculation, Day, null, [_anna], CancellationToken.None);

        spillIn.DatesByClient.Keys.ShouldBe([_anna, _carl], ignoreOrder: true);
    }

    private ScheduleTimelineCheckLoader NewLoader() => new(_context, _calculation);

    private Guid SeedClient(string name)
    {
        var id = Guid.NewGuid();
        _context.Client.Add(new Client { Id = id, Name = name, FirstName = name });
        return id;
    }

    private Guid SeedShift()
    {
        var id = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = id, Name = "Real", Status = ShiftStatus.SplitShift });
        return id;
    }

    private Guid SeedCloneOf(Guid realShiftId)
    {
        var id = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = id,
            Name = "Clone",
            Status = ShiftStatus.SplitShift,
            AnalyseToken = _token,
            ScenarioSourceShiftId = realShiftId,
        });
        return id;
    }

    private Guid SeedWork(Guid clientId, Guid shiftId, DateOnly date, TimeOnly start, TimeOnly end, Guid? analyseToken)
    {
        var id = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 4m,
            AnalyseToken = analyseToken,
        });
        return id;
    }

    private void SeedAbsence(Guid clientId, DateOnly date, Guid? analyseToken)
        => _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = analyseToken,
        });
}
