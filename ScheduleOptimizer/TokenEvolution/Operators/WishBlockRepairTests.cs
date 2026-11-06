// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords.KeywordWishTestData;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The block repair of Wizard 1 (M3 real data 2026-11-06): Lena wishes NIGHT on five days in a row, Amir holds those
/// nights, Lena works day shifts two days before and two days after. With MaxWorkDays 5 and MinRestDays 2 no single
/// exchange helps; the block move gives her the five nights and her day shifts to Amir. It fires when Amir can spare the
/// hours (above his guarantee) and must not fire when it costs him his guaranteed hours, not even when Lena reaches hers
/// by it and the rank-weighted Stage 1 would rise.
/// </summary>
[TestFixture]
public class WishBlockRepairTests
{
    private const string Lena = "lena";
    private const string Amir = "amir";
    private const int BlockStartOffset = 4;
    private const int BlockLength = 5;
    private const int MaxWorkDays = 5;
    private const int MinRestDays = 2;
    private const int PeriodDays = 14;
    private const decimal NightHours = 8;
    private const decimal DayShiftHours = 10;
    private const double FullBlockHours = 40;
    private const double HolderGuaranteeBelowTheBlock = 30;

    private static readonly DateOnly BlockStart = Monday.AddDays(BlockStartOffset);
    private static readonly int[] LenaDayShiftOffsets = [2, 3, 9, 10];

    private static CoreAgent Employee(string id, double guaranteedHours)
        => Agent(id, guaranteedHours) with { MaxWorkDays = MaxWorkDays, MinRestDays = MinRestDays, FullTime = FullBlockHours };

    private static CoreToken Night(string agentId, DateOnly date, Guid shiftId)
    {
        var start = date.ToDateTime(new TimeOnly(23, 0));
        return new CoreToken([], ShiftTypeInference.NightIndex, date, NightHours, start, start.AddHours((double)NightHours),
            Guid.NewGuid(), 0, false, null, shiftId, agentId);
    }

    private static CoreToken DayShift(string agentId, DateOnly date, Guid shiftId)
    {
        var start = date.ToDateTime(new TimeOnly(6, 0));
        return new CoreToken([], ShiftTypeInference.EarlyIndex, date, DayShiftHours, start, start.AddHours((double)DayShiftHours),
            Guid.NewGuid(), 0, false, null, shiftId, agentId);
    }

    private static (CoreWizardContext Context, CoreScenario Plan) LenaBlock(
        double amirGuarantee, int[] lenaDayShiftOffsets, double? lenaGuarantee = null)
    {
        var tokens = new List<CoreToken>();
        var shifts = new List<CoreShift>();
        var commands = new List<CoreScheduleCommand>();
        for (var i = 0; i < BlockLength; i++)
        {
            var date = BlockStart.AddDays(i);
            var shiftId = Guid.NewGuid();
            tokens.Add(Night(Amir, date, shiftId));
            shifts.Add(new CoreShift(shiftId.ToString(), "N", date.ToString("yyyy-MM-dd"), "23:00", "07:00", (double)NightHours, 1, 0));
            commands.Add(new CoreScheduleCommand(Lena, date, ScheduleCommandKeyword.OnlyNight));
        }

        foreach (var offset in lenaDayShiftOffsets)
        {
            var date = Monday.AddDays(offset);
            var shiftId = Guid.NewGuid();
            tokens.Add(DayShift(Lena, date, shiftId));
            shifts.Add(new CoreShift(shiftId.ToString(), "D", date.ToString("yyyy-MM-dd"), "06:00", "16:00", (double)DayShiftHours, 1, 0));
        }

        var context = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(PeriodDays - 1),
            Agents =
            [
                Employee(Lena, lenaGuarantee ?? (double)(DayShiftHours * lenaDayShiftOffsets.Length)),
                Employee(Amir, amirGuarantee),
            ],
            Shifts = shifts,
            ScheduleCommands = commands,
            KeywordMode = KeywordMode.Hard,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };
        return (context, new CoreScenario { Id = "plan", Tokens = tokens });
    }

    [Test]
    public void Apply_FiveNightBlock_FreesTheDaysAroundItAndFulfilsTheWholeBlock()
    {
        var (context, plan) = LenaBlock(amirGuarantee: FullBlockHours, LenaDayShiftOffsets);
        var evaluator = TokenFitnessEvaluator.Create(context);

        var repaired = new WishBlockRepair().Apply(plan, context, evaluator);

        repaired.Tokens.Where(t => t.ShiftTypeIndex == ShiftTypeInference.NightIndex).ShouldAllBe(t => t.AgentId == Lena);
        repaired.Tokens.Where(t => t.ShiftTypeIndex == ShiftTypeInference.EarlyIndex).ShouldAllBe(t => t.AgentId == Amir);
        repaired.FitnessKeywordWishes.Unfulfilled.ShouldBe(0);
        repaired.FitnessStage1.ShouldBeGreaterThanOrEqualTo(plan.FitnessStage1);
    }

    [Test]
    public void ApplyFinalPasses_FiveNightBlock_TracesTheAcceptedBlockMove()
    {
        var (context, plan) = LenaBlock(amirGuarantee: FullBlockHours, LenaDayShiftOffsets);
        var trace = new List<string>();

        var repaired = TokenEvolutionLoop.Create().ApplyFinalPasses(plan, context, new TokenEvolutionConfig(), trace.Add);

        repaired.FitnessKeywordWishes.Unfulfilled.ShouldBe(0);
        trace.ShouldContain("Run: wish repair in the end passes: accepted single swaps=0, accepted block moves=1");
    }

    [Test]
    public void Apply_MoveCostingTheHoldersGuaranteedHours_DoesNotFire()
    {
        var (context, plan) = LenaBlock(amirGuarantee: FullBlockHours, lenaDayShiftOffsets: [2, 3, 9]);

        var repaired = new WishBlockRepair().Apply(plan, context, TokenFitnessEvaluator.Create(context));

        repaired.ShouldBeSameAs(plan);
    }

    [Test]
    public void Apply_HolderAboveTheGuaranteedHours_GivesUpHoursAndTheBlockMoveFires()
    {
        var (context, plan) = LenaBlock(amirGuarantee: HolderGuaranteeBelowTheBlock, lenaDayShiftOffsets: [2, 3, 9]);

        var outcome = new WishBlockRepair().Repair(plan, context, TokenFitnessEvaluator.Create(context));

        outcome.AcceptedMoves.ShouldBe(1);
        var repaired = outcome.Scenario;
        repaired.Tokens.Where(t => t.ShiftTypeIndex == ShiftTypeInference.NightIndex).ShouldAllBe(t => t.AgentId == Lena);
        repaired.Tokens.Where(t => t.ShiftTypeIndex == ShiftTypeInference.EarlyIndex).ShouldAllBe(t => t.AgentId == Amir);
        repaired.FitnessKeywordWishes.Unfulfilled.ShouldBe(0);
        repaired.Tokens.Where(t => t.AgentId == Amir).Sum(t => t.TotalHours).ShouldBeLessThan(NightHours * BlockLength);
    }

    [Test]
    public void Apply_WisherReachingTheGuaranteeWhileTheHolderLosesIt_DoesNotFire()
    {
        var (context, plan) = LenaBlock(amirGuarantee: FullBlockHours, lenaDayShiftOffsets: [2, 3, 9], lenaGuarantee: FullBlockHours);
        var evaluator = TokenFitnessEvaluator.Create(context);
        evaluator.Evaluate(plan, context);
        var swappedBlock = new CoreScenario
        {
            Id = "swapped",
            Tokens = plan.Tokens
                .Select(t => t with { AgentId = t.ShiftTypeIndex == ShiftTypeInference.NightIndex ? Lena : Amir })
                .ToList(),
        };
        evaluator.Evaluate(swappedBlock, context);
        swappedBlock.FitnessStage1.ShouldBeGreaterThan(plan.FitnessStage1);
        evaluator.KeepsEveryGuarantee(swappedBlock, plan, context).ShouldBeFalse();

        var outcome = new WishBlockRepair().Repair(plan, context, evaluator);

        outcome.AcceptedMoves.ShouldBe(0);
        outcome.Scenario.ShouldBeSameAs(plan);
    }

    [Test]
    public void Apply_HolderMayNotTakeTheFreedShifts_DoesNotFire()
    {
        var (context, plan) = LenaBlock(amirGuarantee: FullBlockHours, LenaDayShiftOffsets);
        var blocked = context.Shifts
            .Where(shift => shift.Name == "D")
            .Select(shift => (Amir, Guid.Parse(shift.Id), DateOnly.Parse(shift.Date)))
            .ToHashSet();
        var withAvailability = new CoreWizardContext
        {
            PeriodFrom = context.PeriodFrom,
            PeriodUntil = context.PeriodUntil,
            Agents = context.Agents,
            Shifts = context.Shifts,
            ScheduleCommands = context.ScheduleCommands,
            KeywordMode = context.KeywordMode,
            SchedulingMaxConsecutiveDays = context.SchedulingMaxConsecutiveDays,
            SchedulingMaxDailyHours = context.SchedulingMaxDailyHours,
            SchedulingMinPauseHours = context.SchedulingMinPauseHours,
            IneligibleAssignments = blocked,
        };

        var repaired = new WishBlockRepair().Apply(plan, withAvailability, TokenFitnessEvaluator.Create(withAvailability));

        repaired.ShouldBeSameAs(plan);
    }
}
