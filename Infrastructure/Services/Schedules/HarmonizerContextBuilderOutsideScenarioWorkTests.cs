// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// A scenario of a group run carries only the works on the group's shifts. Anna is in the groups Care and Night and holds a
/// real Night work on a day; the harmonizer (autofill stage 3, Wizard 2 on a scenario) builds its bitmap from the scenario
/// token. That real work must reach the bitmap as a locked occupied cell, or a same-day swap hands Anna a Care shift on a day
/// she already works. A real work on a shift the scenario carries is the scenario's business and must stay out.
/// </summary>
[TestFixture]
public class HarmonizerContextBuilderOutsideScenarioWorkTests
{
    private static readonly DateOnly WeekStart = new(2026, 1, 5);
    private static readonly DateOnly WeekEnd = new(2026, 1, 11);
    private static readonly DateOnly Day = WeekStart.AddDays(2);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);
    private static readonly TimeOnly EarlyStart = new(7, 0);
    private static readonly TimeOnly EarlyEnd = new(15, 0);
    private const decimal ShiftHours = 8m;

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractProvider = null!;
    private IPlanningRuleSetLoader _ruleSetLoader = null!;
    private Guid _token;
    private Guid _anna;
    private Guid _bob;
    private Guid _realCareShift;
    private Guid _scenarioCareShift;
    private Guid _realNightShift;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _ruleSetLoader = Substitute.For<IPlanningRuleSetLoader>();
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], []));

        _token = Guid.NewGuid();
        _anna = Guid.NewGuid();
        _bob = Guid.NewGuid();
        StubContracts(_anna, _bob);
        _realCareShift = SeedShift("Care", EarlyStart, EarlyEnd, analyseToken: null, sourceShiftId: null);
        _scenarioCareShift = SeedShift("Care", EarlyStart, EarlyEnd, _token, _realCareShift);
        _realNightShift = SeedShift("Night", NightStart, NightEnd, analyseToken: null, sourceShiftId: null);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(WorkLockLevel.None)]
    [TestCase(WorkLockLevel.Confirmed)]
    public async Task RealWorkOnAShiftOutsideTheScenario_IsALockedCell_SoNoSwapHandsAnnaASecondShift(WorkLockLevel lockLevel)
    {
        SeedWork(_anna, _realNightShift, Day, NightStart, NightEnd, analyseToken: null, lockLevel);
        SeedWork(_bob, _scenarioCareShift, Day, EarlyStart, EarlyEnd, _token, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(Request(_token), CancellationToken.None);
        var bitmap = BitmapBuilder.Build(input);
        var annaRow = RowOf(bitmap, _anna);
        var bobRow = RowOf(bitmap, _bob);
        var dayIndex = DayIndexOf(bitmap, Day);

        var annaCell = bitmap.GetCell(annaRow, dayIndex);
        annaCell.Symbol.ShouldBe(CellSymbol.Night, "Anna works her Night shift in the other group that day");
        annaCell.IsLocked.ShouldBeTrue("the run must not touch a work outside its scenario");
        annaCell.WorkIds.ShouldBeEmpty("the cell blocks the day, the apply has nothing to re-point");
        annaCell.Hours.ShouldBe(ShiftHours, "the Night shift counts toward Anna's hours");

        var rejection = new PlanMutationValidator(new DomainAwareReplaceValidator(input.Availability))
            .Validate(bitmap, new PlanCellSwap(annaRow, dayIndex, bobRow, dayIndex, "test"));
        rejection.ShouldNotBeNull("a swap would give Anna the Care shift on top of her Night shift");
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.LockedCell);
    }

    [Test]
    public async Task RealWorkOnAShiftTheScenarioCarries_StaysOut_EvenWhenTheScenarioMovedItAway()
    {
        SeedWork(_anna, _realCareShift, Day, EarlyStart, EarlyEnd, analyseToken: null, WorkLockLevel.None);
        SeedWork(_bob, _scenarioCareShift, Day, EarlyStart, EarlyEnd, _token, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(Request(_token), CancellationToken.None);
        var bitmap = BitmapBuilder.Build(input);

        var annaCell = bitmap.GetCell(RowOf(bitmap, _anna), DayIndexOf(bitmap, Day));
        annaCell.Symbol.ShouldBe(CellSymbol.Free, "stage 1 handed Anna's Care work to Bob; the scenario decides the group's shifts");
        annaCell.IsLocked.ShouldBeFalse();
    }

    [Test]
    public async Task RealPlanRun_ReadsAnnasNightWorkOnce_AsTheMovableWorkItIs()
    {
        SeedWork(_anna, _realNightShift, Day, NightStart, NightEnd, analyseToken: null, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(Request(analyseToken: null), CancellationToken.None);

        var annaAssignments = input.Assignments.Where(a => a.AgentId == _anna.ToString() && a.Date == Day).ToList();
        annaAssignments.Count.ShouldBe(1, "without a scenario the work is read once from the plan itself");
        annaAssignments[0].IsLocked.ShouldBeFalse();
        annaAssignments[0].WorkIds.Count.ShouldBe(1);
    }

    [Test]
    public async Task RealWorkOnAShiftOutsideTheScenario_ReachesTheBoundary_ForTheRestRuleAcrossThePeriodEdge()
    {
        var dayBefore = WeekStart.AddDays(-1);
        SeedWork(_anna, _realNightShift, dayBefore, NightStart, NightEnd, analyseToken: null, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(Request(_token), CancellationToken.None);

        input.BoundaryAssignments.ShouldContain(a => a.AgentId == _anna.ToString() && a.Date == dayBefore && a.IsLocked);
    }

    private HarmonizerContextRequest Request(Guid? analyseToken)
        => new(WeekStart, WeekEnd, [_anna, _bob], analyseToken);

    private static int RowOf(HarmonyBitmap bitmap, Guid agent)
        => bitmap.Rows.Select((row, index) => (row, index)).Single(r => r.row.Id == agent.ToString()).index;

    private static int DayIndexOf(HarmonyBitmap bitmap, DateOnly date)
        => bitmap.Days.Select((day, index) => (day, index)).Single(d => d.day == date).index;

    private Guid SeedShift(string name, TimeOnly start, TimeOnly end, Guid? analyseToken, Guid? sourceShiftId)
    {
        var id = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = id,
            Name = name,
            Status = ShiftStatus.SplitShift,
            StartShift = start,
            EndShift = end,
            AnalyseToken = analyseToken,
            ScenarioSourceShiftId = sourceShiftId,
        });
        return id;
    }

    private void SeedWork(Guid agent, Guid shiftId, DateOnly date, TimeOnly start, TimeOnly end, Guid? analyseToken, WorkLockLevel lockLevel)
        => _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = ShiftHours,
            AnalyseToken = analyseToken,
            LockLevel = lockLevel,
        });

    private void StubContracts(params Guid[] agents)
    {
        var data = new EffectiveContractData
        {
            GuaranteedHours = 160m,
            PaymentInterval = (int)PaymentInterval.Monthly,
            MaxConsecutiveDays = 7,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>())
            .Returns(agents.ToDictionary(agent => agent, _ => data));

        var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            range[date] = agents.ToDictionary(agent => agent, _ => data);
        }
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(range);

        foreach (var agent in agents)
        {
            _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = agent.ToString() });
        }
        _context.SaveChanges();
    }

    private HarmonizerContextBuilder BuildSut()
    {
        var softening = Substitute.For<IWorkSofteningRepository>();
        softening.LoadAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkSoftening>>([]));

        var eligibility = Substitute.For<IEligibilityMatrixBuilder>();
        eligibility.BuildAsync(default!, default!, default!, default).ReturnsForAnyArgs(new EligibilityMatrix
        {
            Ineligible = new HashSet<(string, Guid, DateOnly)>(),
            Gaps = new Dictionary<(string, Guid, DateOnly), IReadOnlyList<QualificationGap>>(),
            QualificationInfo = new Dictionary<Guid, QualificationInfo>(),
            ShiftNames = new Dictionary<Guid, string>(),
        });

        var availability = Substitute.For<IAvailabilityIneligibilityService>();
        availability.GetAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlySet<(string, Guid, DateOnly)>>(new HashSet<(string, Guid, DateOnly)>()));

        var windows = Substitute.For<IWizardRestrictedWindowBuilder>();
        windows.BuildAsync(default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<CoreRestrictedTimeWindow>>([]));

        var keywords = Substitute.For<IScheduleCommandKeywordProvider>();
        keywords.GetAsync(default).ReturnsForAnyArgs(ScheduleCommandKeywordTestFactory.Default);

        return new HarmonizerContextBuilder(_context, _contractProvider, softening, eligibility, availability, windows, keywords, _ruleSetLoader);
    }
}
