// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

/// <summary>
/// Pins what stage 1 decides about the shape of the hour distribution, because it decides it for every
/// pass that moves hours between employees. The stage is a binary flag per employee — reached the
/// guaranteed hours or not — weighted by roster rank, and it is the third key of the lexicographic
/// comparison. Wherever coverage and legality are already at their optimum, stage 1 alone therefore
/// picks the winner, and a plan with fewer reached guarantees can never win, however evenly it spreads
/// what is left. The consequence is measured in the test below: a "no employee under 95 % of its
/// guarantee" distribution is by construction a distribution with FEWER reached guarantees, so no
/// operator at any gating granularity can produce one. Lifting the floor is a decision about stage 1,
/// not about an operator.
/// </summary>
[TestFixture]
public class Stage1GuaranteeDominanceTests
{
    private const double GuaranteedHours = 100;
    private const decimal ShiftHours = 5;
    private const int ShiftsPerGuarantee = 20;
    private const int ShiftsAtNinetyFivePercent = 19;
    private const int SupplyShifts = 57;
    private const int RosterSize = 3;

    private static readonly DateOnly FirstDay = new(2026, 3, 2);

    /// <summary>
    /// Roster of three identical employees over a period long enough to hold the whole supply without a
    /// single rule violation, so that both stage-0 keys of the comparison are zero on every plan and the
    /// comparison provably turns on stage 1.
    /// </summary>
    private static CoreWizardContext Context()
    {
        var shifts = new List<CoreShift>(SupplyShifts);
        for (var i = 0; i < SupplyShifts; i++)
        {
            shifts.Add(new CoreShift(
                Id: Slot(i).ToString(),
                Name: "Early",
                Date: FirstDay.AddDays(i).ToString("yyyy-MM-dd"),
                StartTime: "08:00",
                EndTime: "13:00",
                Hours: (double)ShiftHours,
                RequiredAssignments: 1,
                Priority: 0));
        }

        return new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = FirstDay.AddDays(SupplyShifts - 1),
            Agents = Enumerable.Range(1, RosterSize).Select(i => Agent($"MA-{i}")).ToList(),
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = SupplyShifts,
            SchedulingMinPauseHours = 0,
        };
    }

    /// <summary>Order identity of one slot; the numbering starts at one because the empty id means "no order".</summary>
    /// <param name="index">Position of the slot in the supply</param>
    private static Guid Slot(int index) => new($"00000000-0000-0000-0000-{index + 1:D12}");

    private static CoreAgent Agent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: GuaranteedHours,
        MaxConsecutiveDays: SupplyShifts,
        MinRestHours: 0,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = GuaranteedHours,
        MaxWorkDays = SupplyShifts,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    /// <summary>
    /// Builds the plan that gives the employees the stated number of shifts, top of the roster first,
    /// out of the same supply of slots in the same order — the two plans therefore differ in nothing but
    /// who holds which slot.
    /// </summary>
    /// <param name="id">Plan identifier</param>
    /// <param name="shiftsPerAgent">Shifts the employee of each roster position holds</param>
    private static CoreScenario Plan(string id, params int[] shiftsPerAgent)
    {
        var tokens = new List<CoreToken>(SupplyShifts);
        var slot = 0;
        for (var agent = 0; agent < shiftsPerAgent.Length; agent++)
        {
            for (var i = 0; i < shiftsPerAgent[agent]; i++, slot++)
            {
                var date = FirstDay.AddDays(slot);
                var start = new TimeOnly(8, 0);
                tokens.Add(new CoreToken(
                    WorkIds: [],
                    ShiftTypeIndex: 0,
                    Date: date,
                    TotalHours: ShiftHours,
                    StartAt: date.ToDateTime(start),
                    EndAt: date.ToDateTime(start.AddHours((double)ShiftHours)),
                    BlockId: Guid.NewGuid(),
                    PositionInBlock: 0,
                    IsLocked: false,
                    LocationContext: null,
                    ShiftRefId: Slot(slot),
                    AgentId: $"MA-{agent + 1}"));
            }
        }

        return new CoreScenario { Id = id, Tokens = tokens };
    }

    /// <summary>
    /// The supply covers two guarantees and a remainder. Top-down keeps two employees at their guarantee
    /// and leaves the rest to the last rank; the alternative puts every employee at 95 % of its guarantee
    /// and reaches none. Both are legal, both are fully covered, both use the identical slots — and the
    /// comparison prefers the top-down plan, because two reached guarantees outrank three near misses.
    /// </summary>
    [Test]
    public void Compare_EveryRankAtNinetyFivePercent_LosesAgainstTwoReachedGuarantees()
    {
        var context = Context();
        var evaluator = TokenFitnessEvaluator.Create(context);
        var remainder = SupplyShifts - (2 * ShiftsPerGuarantee);
        var topDown = Plan("top-down", ShiftsPerGuarantee, ShiftsPerGuarantee, remainder);
        var everyRankInTheBand = Plan(
            "band", ShiftsAtNinetyFivePercent, ShiftsAtNinetyFivePercent, ShiftsAtNinetyFivePercent);

        evaluator.Evaluate(topDown, context);
        evaluator.Evaluate(everyRankInTheBand, context);

        topDown.FitnessStage0Legality.ShouldBe(0);
        topDown.FitnessStage0.ShouldBe(0);
        everyRankInTheBand.FitnessStage0Legality.ShouldBe(0);
        everyRankInTheBand.FitnessStage0.ShouldBe(0);
        everyRankInTheBand.FitnessStage1.ShouldBeLessThan(topDown.FitnessStage1);
        evaluator.Compare(everyRankInTheBand, topDown).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The same statement one step further down: the band plan does not even win the stage the band was
    /// supposed to serve. Stage 2 measures the distance to the target with the same top-down decay, so
    /// spreading the shortfall over the better ranks costs more there than it gains at the bottom. The
    /// band therefore has no fitness key on its side at all.
    /// </summary>
    [Test]
    public void Compare_EveryRankAtNinetyFivePercent_DoesNotWinStageTwoEither()
    {
        var context = Context();
        var evaluator = TokenFitnessEvaluator.Create(context);
        var remainder = SupplyShifts - (2 * ShiftsPerGuarantee);
        var topDown = Plan("top-down", ShiftsPerGuarantee, ShiftsPerGuarantee, remainder);
        var everyRankInTheBand = Plan(
            "band", ShiftsAtNinetyFivePercent, ShiftsAtNinetyFivePercent, ShiftsAtNinetyFivePercent);

        evaluator.Evaluate(topDown, context);
        evaluator.Evaluate(everyRankInTheBand, context);

        everyRankInTheBand.FitnessStage2.ShouldBeLessThan(topDown.FitnessStage2);
    }
}
