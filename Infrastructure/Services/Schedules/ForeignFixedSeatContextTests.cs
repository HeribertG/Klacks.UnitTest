// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Wizard 1 must not staff a seat twice that a person outside the run already holds with a fixed work: the apply keeps
/// that work (CorrectedWorkQuery.LoadFixedWorkIdsAsync, LockLevel above None), so the context must not offer the seat.
/// Runs the real WizardShiftBuilder and WizardHardConstraintBuilder on an in-memory database into the WizardContextBuilder.
/// Example: group B needs one person in the early shift on 10 March; Anna from the sister group holds it (locked, or moved
/// to her by an absence cover) and is no agent of the run; without the fix the context offered the seat and Maria was set
/// on top.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ForeignFixedSeatContextTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly DateOnly OtherDay = new(2026, 3, 11);

    private DataBaseContext _context = null!;
    private IWizardWarmStartBuilder _warmStart = null!;
    private readonly Guid _maria = Guid.NewGuid();
    private readonly Guid _anna = Guid.NewGuid();
    private readonly Guid _absent = Guid.NewGuid();
    private Guid _earlyShift;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _warmStart = Substitute.For<IWizardWarmStartBuilder>();
        _warmStart
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreWarmStartAssignment>)new List<CoreWarmStartAssignment>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task LockedWorkOfAPersonOutsideTheRun_TakesTheSeatOutOfTheContext()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.Confirmed);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(0);
        EvaluationContext.For(context).SlotCapacities.ShouldNotContainKey((_earlyShift, Day));
        context.LockedWorks.ShouldBeEmpty();
    }

    [Test]
    public async Task MovedWorkOfAPersonOutsideTheRun_TakesTheSeatOutOfTheContext()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.None, movedFrom: _absent);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(0);
        EvaluationContext.For(context).SlotCapacities.ShouldNotContainKey((_earlyShift, Day));
    }

    [TestCase(WorkLockLevel.Confirmed, false)]
    [TestCase(WorkLockLevel.None, true)]
    public async Task DemandOfTwo_WithOneFixedWorkOutsideTheRun_LeavesExactlyOneSeat(WorkLockLevel lockLevel, bool moved)
    {
        _earlyShift = AddShift(quantity: 2, token: null);
        AddWork(_anna, Day, null, lockLevel, movedFrom: moved ? _absent : null);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(1);
        SeatsOn(context, OtherDay).ShouldBe(2);
        EvaluationContext.For(context).SlotCapacities[(_earlyShift, Day)].ShouldBe(1);
    }

    [Test]
    public async Task FixedWorkOfAnAgent_StaysALockedTokenOnAnUnchangedSeat_NotSubtractedTwice()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        var locked = AddWork(_maria, Day, null, WorkLockLevel.Confirmed);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(1);
        EvaluationContext.For(context).SlotCapacities[(_earlyShift, Day)].ShouldBe(1);
        var token = context.LockedWorks.ShouldHaveSingleItem();
        token.WorkId.ShouldBe(locked.Id.ToString());
        token.ShiftRefId.ShouldBe(_earlyShift);
        token.Date.ShouldBe(Day);
    }

    [Test]
    public async Task UnlockedWorkOfAPersonOutsideTheRun_KeepsTheSeat()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(1);
    }

    [Test]
    public async Task TwoFixedWorksOutsideTheRun_OnADemandOfOne_LeaveNoSeat_AndNothingNegative()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.Confirmed);
        AddWork(Guid.NewGuid(), Day, null, WorkLockLevel.None, movedFrom: _absent);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(0);
        SeatsOn(context, OtherDay).ShouldBe(1);
        EvaluationContext.For(context).SlotCapacities.Values.ShouldAllBe(capacity => capacity > 0);
    }

    [Test]
    public async Task FixedWorkOutsideTheRun_OnAShiftOrDayOutsideTheRun_HasNoEffect()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        var otherShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.Confirmed, shiftId: otherShift);
        AddWork(_anna, Day.AddDays(7), null, WorkLockLevel.Confirmed);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null, shiftIds: [_earlyShift]);

        SeatsOn(context, Day).ShouldBe(1);
        SeatsOn(context, OtherDay).ShouldBe(1);
    }

    [Test]
    public async Task ReplacementOfAPersonOutsideTheRun_ByAnAgent_TakesTheSeatOnce_AndTheAgentStaysOccupied()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        var covered = AddWork(_anna, Day, null, WorkLockLevel.None);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = covered.Id,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = _maria,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            ChangeTime = 2m,
        });
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(0);
        context.LockedWorks.ShouldBeEmpty();
        context.ExistingWorkBlockers.ShouldContain(b => b.AgentId == _maria.ToString() && b.Date == Day);
    }

    [Test]
    public async Task ScenarioRun_CountsTheFixedWorksOfTheScenario_NotThoseOfTheRealPlan()
    {
        var token = Guid.NewGuid();
        _earlyShift = AddShift(quantity: 1, token: token);
        var realShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, token, WorkLockLevel.None, movedFrom: _absent);
        AddWork(_anna, OtherDay, null, WorkLockLevel.Confirmed, shiftId: realShift);
        AddWork(_anna, OtherDay, null, WorkLockLevel.Confirmed);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(token);

        SeatsOn(context, Day).ShouldBe(0);
        SeatsOn(context, OtherDay).ShouldBe(1);
        context.Shifts.ShouldAllBe(s => s.Id == _earlyShift.ToString());
    }

    [Test]
    public async Task WarmStartAssignment_OnASlotWithoutSeat_IsDropped_OthersKept()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.Confirmed);
        await _context.SaveChangesAsync();
        var onHeldSlot = WarmStart(Day);
        var onOpenSlot = WarmStart(OtherDay);
        _warmStart
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreWarmStartAssignment>)new List<CoreWarmStartAssignment> { onHeldSlot, onOpenSlot });

        var context = await BuildContextAsync(null);

        context.WarmStartAssignments.ShouldBe(new[] { onOpenSlot });
    }

    [Test]
    public async Task WarmStartAssignment_OnAWeekdayTheShiftDoesNotRun_IsDropped()
    {
        _earlyShift = AddShift(quantity: 1, token: null, runsOnWednesday: false);
        await _context.SaveChangesAsync();
        var onRunningDay = WarmStart(Day);
        var onDayOff = WarmStart(OtherDay);
        _warmStart
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreWarmStartAssignment>)new List<CoreWarmStartAssignment> { onRunningDay, onDayOff });

        var context = await BuildContextAsync(null);

        context.WarmStartAssignments.ShouldBe(new[] { onRunningDay });
    }

    [Test]
    public async Task SoftDeletedFixedWorkOutsideTheRun_KeepsTheSeat()
    {
        _earlyShift = AddShift(quantity: 1, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.Confirmed).IsDeleted = true;
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(1);
    }

    [Test]
    public async Task ReplacementOfAnAgent_ByAPersonOutsideTheRun_HoldsExactlyOneSeat()
    {
        _earlyShift = AddShift(quantity: 2, token: null);
        var covered = AddWork(_maria, Day, null, WorkLockLevel.None);
        AddReplacement(covered, substitute: _anna, token: null);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(2);
        context.LockedWorks.ShouldHaveSingleItem().WorkId.ShouldBe(covered.Id.ToString());
        OpenSeatsOn(context, Day).ShouldBe(1);
    }

    [Test]
    public async Task LockedWorkOfAnAgent_AndAFixedWorkOutsideTheRun_OnADemandOfTwo_LeaveNothingOpen()
    {
        _earlyShift = AddShift(quantity: 2, token: null);
        AddWork(_maria, Day, null, WorkLockLevel.Confirmed);
        AddWork(_anna, Day, null, WorkLockLevel.None, movedFrom: _absent);
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null);

        SeatsOn(context, Day).ShouldBe(1);
        context.LockedWorks.ShouldHaveSingleItem();
        OpenSeatsOn(context, Day).ShouldBe(0);
        Violations(context, []).ShouldNotContain(v => v.Kind == ViolationKind.OverSupply);
    }

    [Test]
    public async Task ContainerChildAndSplitPartOutsideTheRun_EachHoldASeat_PerRowLikeTheAgentPath()
    {
        _earlyShift = AddShift(quantity: 3, token: null);
        var containerShift = AddShift(quantity: 1, token: null);
        var container = AddWork(_anna, Day, null, WorkLockLevel.Confirmed, shiftId: containerShift);
        AddWork(_anna, Day, null, WorkLockLevel.Confirmed).ParentWorkId = container.Id;
        var firstPart = AddWork(Guid.NewGuid(), OtherDay, null, WorkLockLevel.Confirmed);
        AddWork(firstPart.ClientId, OtherDay, null, WorkLockLevel.Confirmed).SplitFromWorkId = firstPart.Id;
        await _context.SaveChangesAsync();

        var context = await BuildContextAsync(null, shiftIds: [_earlyShift]);

        SeatsOn(context, Day).ShouldBe(2);
        SeatsOn(context, OtherDay).ShouldBe(1);
    }

    [Test]
    public async Task CorrectedWorkOutsideTheRun_CountsOnlyInTheWorldOfItsToken()
    {
        var token = Guid.NewGuid();
        _earlyShift = AddShift(quantity: 1, token: token);
        var inScenario = AddWork(_anna, Day, token, WorkLockLevel.None);
        AddCorrection(inScenario, token);
        var real = AddWork(_anna, OtherDay, null, WorkLockLevel.None);
        AddCorrection(real, token: null);
        await _context.SaveChangesAsync();

        var scenario = await BuildContextAsync(token);

        SeatsOn(scenario, Day).ShouldBe(0);
        SeatsOn(scenario, OtherDay).ShouldBe(1);
    }

    [Test]
    public async Task ObjectiveOfTheReducedContext_DemandAndCoverageDropByTheHeldSeat_TheComparisonStaysConsistent()
    {
        _earlyShift = AddShift(quantity: 2, token: null);
        AddWork(_anna, Day, null, WorkLockLevel.Confirmed);
        await _context.SaveChangesAsync();
        var context = await BuildContextAsync(null);
        var mariaOnTheOpenSeat = new AssignmentView(
            _maria.ToString(), Day, _earlyShift, 0, 8m, Day.ToDateTime(new TimeOnly(6, 0)), Day.ToDateTime(new TimeOnly(14, 0)), null, false);

        var empty = Violations(context, []);
        var staffed = Violations(context, [mariaOnTheOpenSeat]);

        empty.Count(v => v.Kind == ViolationKind.UnderSupply && v.Date == Day).ShouldBe(1);
        staffed.Count(v => v.Kind == ViolationKind.UnderSupply && v.Date == Day).ShouldBe(0);
        staffed.ShouldNotContain(v => v.Kind == ViolationKind.OverSupply);
    }

    private static IReadOnlyList<ConstraintViolation> Violations(CoreWizardContext context, IReadOnlyList<AssignmentView> plan)
        => new PlanConstraintChecker().Check(
            plan.Concat(context.LockedWorks.Select(w => new AssignmentView(
                w.AgentId, w.Date, w.ShiftRefId, w.ShiftTypeIndex, w.TotalHours, w.StartAt, w.EndAt, null, true))).ToList(),
            context);

    private static int OpenSeatsOn(CoreWizardContext context, DateOnly date)
        => Violations(context, []).Count(v => v.Kind == ViolationKind.UnderSupply && v.Date == date);

    private void AddReplacement(Work covered, Guid substitute, Guid? token)
        => _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = covered.Id,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = substitute,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            ChangeTime = 2m,
            AnalyseToken = token,
        });

    private void AddCorrection(Work work, Guid? token)
        => _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd,
            StartTime = new TimeOnly(14, 0),
            EndTime = new TimeOnly(15, 0),
            ChangeTime = 1m,
            AnalyseToken = token,
        });

    private CoreWarmStartAssignment WarmStart(DateOnly date) => new(
        _maria.ToString(), date, _earlyShift, date.ToDateTime(new TimeOnly(6, 0)), date.ToDateTime(new TimeOnly(14, 0)), 8m);

    private static int SeatsOn(CoreWizardContext context, DateOnly date)
        => context.Shifts.Count(s => s.Date == date.ToString(DateTimeFormats.IsoDate, CultureInfo.InvariantCulture));

    private async Task<CoreWizardContext> BuildContextAsync(Guid? token, IReadOnlyList<Guid>? shiftIds = null)
    {
        var contractProvider = Substitute.For<IClientContractDataProvider>();
        var contract = new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            FullTime = 40,
            GuaranteedHours = 30,
            MaxDailyHours = 10,
            MaxWeeklyHours = 50,
            MinPauseHours = 11,
            MaxConsecutiveDays = 6,
        };
        var contracts = new Dictionary<Guid, EffectiveContractData> { [_maria] = contract };
        contractProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(contract);
        contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(contracts);
        contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                var byDay = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
                for (var date = ci.ArgAt<DateOnly>(1); date <= ci.ArgAt<DateOnly>(2); date = date.AddDays(1))
                {
                    byDay[date] = contracts;
                }

                return byDay;
            });
        var membershipReader = Substitute.For<IMembershipWindowReader>();
        membershipReader
            .GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, MembershipWindow>)new Dictionary<Guid, MembershipWindow>());
        var periodHours = Substitute.For<IPeriodHoursService>();
        periodHours
            .GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns(ci => (ci.Arg<DateOnly>(), ci.Arg<DateOnly>()));
        periodHours
            .GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>());
        var eligibility = Substitute.For<IEligibilityMatrixBuilder>();
        eligibility
            .BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
                Arg.Any<CancellationToken>())
            .Returns(EligibilityMatrix.Empty);
        var availability = Substitute.For<IAvailabilityIneligibilityService>();
        availability
            .GetAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<AvailabilityShiftSlot>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<(string, Guid, DateOnly)>)new HashSet<(string, Guid, DateOnly)>());
        var windows = Substitute.For<IWizardRestrictedWindowBuilder>();
        windows
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreRestrictedTimeWindow>)new List<CoreRestrictedTimeWindow>());
        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository
            .GetStandInIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);

        var builder = new WizardContextBuilder(
            new WizardAgentSnapshotBuilder(contractProvider, membershipReader),
            new WizardShiftBuilder(_context),
            new WizardHardConstraintBuilder(_context, keywordProvider),
            periodHours,
            contractProvider,
            eligibility,
            availability,
            _warmStart,
            windows,
            clientRepository);

        return await builder.BuildContextAsync(
            new WizardContextRequest(Day, OtherDay, [_maria], shiftIds, token), CancellationToken.None);
    }

    private Guid AddShift(int quantity, Guid? token, bool runsOnWednesday = true)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Early",
            Abbreviation = "E",
            FromDate = Day,
            UntilDate = OtherDay,
            StartShift = new TimeOnly(6, 0),
            EndShift = new TimeOnly(14, 0),
            WorkTime = 8m,
            Quantity = quantity,
            IsTuesday = true,
            IsWednesday = runsOnWednesday,
            AnalyseToken = token,
        };
        _context.Shift.Add(shift);
        return shift.Id;
    }

    private Work AddWork(Guid client, DateOnly date, Guid? token, WorkLockLevel lockLevel, Guid? movedFrom = null, Guid? shiftId = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            ShiftId = shiftId ?? _earlyShift,
            CurrentDate = date,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
            WorkTime = 8m,
            LockLevel = lockLevel,
            AnalyseToken = token,
            MovedFromClientId = movedFrom,
        };
        _context.Work.Add(work);
        return work;
    }
}
