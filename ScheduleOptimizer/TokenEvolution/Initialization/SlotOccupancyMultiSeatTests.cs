// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins multi-seat shift days (Quantity x SumEmployees single-seat slots of one shift on one date): a locked work
/// covers exactly ONE of those slots, so seeding and repair must still staff the remaining seats instead of treating
/// the whole shift day as satisfied.
/// </summary>

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class SlotOccupancyMultiSeatTests
{
    private const int Seats = 3;
    private const string LockedAgentId = "L";
    private static readonly DateOnly Day = new(2026, 4, 20);
    private static readonly Guid ShiftId = Guid.NewGuid();

    private static CoreAgent Agent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreShift Seat() => new(ShiftId.ToString(), "FD", "2026-04-20", "08:00", "16:00", 8, 1, 0);

    private static CoreLockedWork LockedSeat() => new(
        "w1", LockedAgentId, Day, 0, 8, Day.ToDateTime(new TimeOnly(8, 0)), Day.ToDateTime(new TimeOnly(16, 0)), ShiftId, null);

    private static CoreWizardContext Context(bool withLockedSeat) => new()
    {
        PeriodFrom = Day,
        PeriodUntil = Day,
        Agents = [Agent(LockedAgentId), Agent("A"), Agent("B"), Agent("C")],
        Shifts = Enumerable.Range(0, Seats).Select(_ => Seat()).ToList(),
        LockedWorks = withLockedSeat ? [LockedSeat()] : [],
    };

    [Test]
    public void TryClaim_OneLockedWork_CoversExactlyOneSeat()
    {
        var occupancy = SlotOccupancy.Of([LockedSeat()]);

        occupancy.TryClaim(Seat()).ShouldBeTrue();
        occupancy.TryClaim(Seat()).ShouldBeFalse();
        occupancy.TryClaim(Seat()).ShouldBeFalse();
    }

    [Test]
    public void CoverageFirst_OneLockedSeatOfThree_StaffsTheOtherTwo()
    {
        var scenario = new CoverageFirstTokenStrategy().BuildScenario(Context(withLockedSeat: true), new Random(0));

        scenario.Tokens.Count(t => t.ShiftRefId == ShiftId && t.Date == Day).ShouldBe(Seats);
    }

    [Test]
    public void Greedy_OneLockedSeatOfThree_StaffsTheOtherTwo()
    {
        var scenario = new GreedyTokenStrategy().BuildScenario(Context(withLockedSeat: true), new Random(0));

        scenario.Tokens.Count(t => t.ShiftRefId == ShiftId && t.Date == Day).ShouldBe(Seats);
    }

    [Test]
    public void FirstPlannableDay_OneLockedSeatOfThree_IsThatDay()
    {
        CarryInContinuation.FirstPlannableDay(Context(withLockedSeat: true)).ShouldBe(Day);
    }

    [Test]
    public void TokenRepair_EmptyMultiSeatShiftDay_FillsEverySeat()
    {
        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var repaired = new TokenRepair(new TokenConstraintChecker())
            .FillAllUnderSupply(scenario, Context(withLockedSeat: false), new Random(0));

        repaired.Tokens.Count(t => t.ShiftRefId == ShiftId && t.Date == Day).ShouldBe(Seats);
    }
}
