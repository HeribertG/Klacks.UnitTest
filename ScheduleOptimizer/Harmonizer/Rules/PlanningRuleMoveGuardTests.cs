// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;

/// <summary>
/// The guard must give exactly the verdict of the pre-commit delta (per hard rule and agent: summed Excess must not
/// grow), computed here plan-wide on an independent oracle plan before and after the move. The move generator covers
/// same-day swaps, cross-day swaps between rows and within one row, a free cell received in the middle of a night
/// block, merged two-work cells, boundary nights, week carry-in through the boundary and month carry-in beyond it.
/// </summary>
[TestFixture]
public class PlanningRuleMoveGuardTests
{
    private const int MovesPerSeed = 400;
    private static readonly int[] Seeds = Enumerable.Range(1, 10).ToArray();

    [TestCaseSource(nameof(Seeds))]
    public void Diagnose_AgreesWithPlanWideDelta_OnRandomMoves(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.HardRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var guard = new PlanningRuleMoveGuard(runtime);
        var context = PlanningRuleBitmapFixture.Context(input);
        var evaluator = PlanRuleEvaluatorFactory.Create(input.Rules!.Rules, context);
        var bitmap = BitmapBuilder.Build(input);
        var random = new Random(seed * 7919);
        var rejected = 0;

        for (var move = 0; move < MovesPerSeed; move++)
        {
            var (rowA, dayA, rowB, dayB) = RandomMove(bitmap, random);
            var before = PlanningRuleBitmapFixture.HardExcess(evaluator.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, bitmap)));
            var after = BitmapCloner.Clone(bitmap);
            PlanningRuleBitmapFixture.Swap(after, rowA, dayA, rowB, dayB);
            var afterExcess = PlanningRuleBitmapFixture.HardExcess(evaluator.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, after)));

            var cellA = bitmap.GetCell(rowA, dayA);
            var cellB = bitmap.GetCell(rowB, dayB);
            string? verdict;
            if (rowA == rowB)
            {
                verdict = guard.Diagnose(bitmap, rowA, [(dayA, cellB), (dayB, cellA)], "rowA");
            }
            else
            {
                verdict = guard.Diagnose(bitmap, rowA, [(dayA, cellB)], "rowA") ?? guard.Diagnose(bitmap, rowB, [(dayB, cellA)], "rowB");
            }

            var expectedReject = Worsened(before, afterExcess, bitmap.Rows[rowA].Id) || Worsened(before, afterExcess, bitmap.Rows[rowB].Id);
            (verdict is not null).ShouldBe(expectedReject, $"seed {seed} move {move}: r{rowA}d{dayA}<->r{rowB}d{dayB} verdict '{verdict}'");
            if (verdict is not null)
            {
                rejected++;
            }
            else if (random.NextDouble() < 0.5)
            {
                PlanningRuleBitmapFixture.Swap(bitmap, rowA, dayA, rowB, dayB);
            }
        }

        rejected.ShouldBeGreaterThan(0, "the generator must produce moves the rules reject");
    }

    [Test]
    public void Diagnose_PreexistingCounterExcess_DoesNotBlockANightForNightSwap()
    {
        var input = PlanningRuleBitmapFixture.Build(4, [new PeriodCountRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleCounterEvent.NightShift, RuleCalendarPeriod.Month, 1)]);
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var guard = new PlanningRuleMoveGuard(runtime);
        var bitmap = BitmapBuilder.Build(input);
        var (rowA, rowB, day) = FindSameDayPair(bitmap, CellSymbol.Night, CellSymbol.Night, requireDifferentShift: false);

        runtime.Evaluate(bitmap).HardCount.ShouldBeGreaterThan(0, "threshold 1 is met before any move");
        guard.Diagnose(bitmap, rowA, [(day, bitmap.GetCell(rowB, day))], "rowA").ShouldBeNull();
    }

    [Test]
    public void Diagnose_FreeCellInTheMiddleOfANightBlock_IsRejectedByRestAfter()
    {
        var restAfter = new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 2);
        var nightAt = PlanningRuleBitmapFixture.From.ToDateTime(new TimeOnly(23, 0), DateTimeKind.Utc);
        var agents = Enumerable.Range(0, 2)
            .Select(i => new BitmapAgent(PlanningRuleBitmapFixture.AgentId(i), PlanningRuleBitmapFixture.AgentId(i), 0m, new HashSet<CellSymbol>(),
                NightWindow: PlanningRuleBitmapFixture.NightWindow))
            .ToList();
        var assignments = new List<BitmapAssignment>();
        for (var day = 0; day < 3; day++)
        {
            var start = nightAt.AddDays(day);
            assignments.Add(new BitmapAssignment(agents[0].Id, PlanningRuleBitmapFixture.From.AddDays(day), CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false, start, start.AddHours(8), 8m));
        }

        var input = new BitmapInput(agents, PlanningRuleBitmapFixture.From, PlanningRuleBitmapFixture.From.AddDays(4), assignments,
            Rules: new BitmapPlanningRules([restAfter], [], PlanningRuleBitmapFixture.NightMinOverlap));
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var bitmap = BitmapBuilder.Build(input);

        runtime.Evaluate(bitmap).HardCount.ShouldBe(0);
        var verdict = new PlanningRuleMoveGuard(runtime).Diagnose(bitmap, 0, [(1, bitmap.GetCell(1, 1))], "rowA");

        verdict.ShouldNotBeNull();
        verdict.ShouldContain(nameof(PlanRuleKind.RestAfterKind));
    }

    [Test]
    public void PlanMutationValidator_CrossDaySwapWithinOneRow_IsJudgedOnTheCombinedEffect()
    {
        var maxRun = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 2);
        var nightAt = PlanningRuleBitmapFixture.From.ToDateTime(new TimeOnly(23, 0), DateTimeKind.Utc);
        var agent = new BitmapAgent("solo", "solo", 0m, new HashSet<CellSymbol>(), NightWindow: PlanningRuleBitmapFixture.NightWindow);
        var lateAt = PlanningRuleBitmapFixture.From.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc);
        var assignments = new List<BitmapAssignment>
        {
            new(agent.Id, PlanningRuleBitmapFixture.From, CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false, nightAt, nightAt.AddHours(8), 8m),
            new(agent.Id, PlanningRuleBitmapFixture.From.AddDays(1), CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false, nightAt.AddDays(1), nightAt.AddDays(1).AddHours(8), 8m),
            new(agent.Id, PlanningRuleBitmapFixture.From.AddDays(2), CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false, lateAt.AddDays(2), lateAt.AddDays(2).AddHours(8), 8m),
            new(agent.Id, PlanningRuleBitmapFixture.From.AddDays(3), CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false, nightAt.AddDays(3), nightAt.AddDays(3).AddHours(8), 8m),
        };
        var input = new BitmapInput([agent], PlanningRuleBitmapFixture.From, PlanningRuleBitmapFixture.From.AddDays(4), assignments,
            Rules: new BitmapPlanningRules([maxRun], [], PlanningRuleBitmapFixture.NightMinOverlap));
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var validator = new PlanMutationValidator(DomainAwareReplaceValidator.ForInput(input, runtime));
        var bitmap = BitmapBuilder.Build(input);

        var rejection = validator.Validate(bitmap, new PlanCellSwap(0, 2, 0, 3, string.Empty));

        rejection.ShouldNotBeNull("N N L N -> N N N L makes a run of three nights");
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        validator.Validate(bitmap, new PlanCellSwap(0, 1, 0, 2, string.Empty)).ShouldBeNull("N L N N keeps every run at two");
    }

    [Test]
    public void DomainAwareReplaceValidator_SameDaySwap_RejectsAHardRuleViolationOnEitherSide()
    {
        var input = PlanningRuleBitmapFixture.Build(6, PlanningRuleBitmapFixture.HardRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var guard = new PlanningRuleMoveGuard(runtime);
        var withRules = DomainAwareReplaceValidator.ForInput(input, runtime);
        var withoutRules = DomainAwareReplaceValidator.ForInput(input, null);
        var bitmap = BitmapBuilder.Build(input);
        var seenRuleReject = false;

        for (var day = 0; day < bitmap.DayCount; day++)
        {
            for (var rowA = 0; rowA < bitmap.RowCount; rowA++)
            {
                for (var rowB = rowA + 1; rowB < bitmap.RowCount; rowB++)
                {
                    var move = new ReplaceMove(rowA, rowB, day);
                    var plain = withoutRules.Diagnose(bitmap, move);
                    var ruled = withRules.Diagnose(bitmap, move);
                    var expected = plain ?? guard.Diagnose(bitmap, rowA, [(day, bitmap.GetCell(rowB, day))], "rowA")
                        ?? guard.Diagnose(bitmap, rowB, [(day, bitmap.GetCell(rowA, day))], "rowB");
                    ruled.ShouldBe(expected);
                    seenRuleReject |= plain is null && ruled is not null;
                }
            }
        }

        seenRuleReject.ShouldBeTrue();
    }

    private static bool Worsened(
        Dictionary<(Guid RuleId, string? AgentId), decimal> before,
        Dictionary<(Guid RuleId, string? AgentId), decimal> after,
        string agentId)
        => after.Any(entry => entry.Key.AgentId == agentId && entry.Value > before.GetValueOrDefault(entry.Key));

    private static (int RowA, int DayA, int RowB, int DayB) RandomMove(HarmonyBitmap bitmap, Random random)
    {
        while (true)
        {
            var rowA = random.Next(bitmap.RowCount);
            var dayA = random.Next(bitmap.DayCount);
            var kind = random.Next(3);
            var rowB = kind == 2 ? rowA : random.Next(bitmap.RowCount);
            var dayB = kind == 0 ? dayA : random.Next(bitmap.DayCount);
            if ((rowA == rowB && dayA == dayB) || bitmap.GetCell(rowA, dayA).IsLocked || bitmap.GetCell(rowB, dayB).IsLocked)
            {
                continue;
            }

            return (rowA, dayA, rowB, dayB);
        }
    }

    private static (int RowA, int RowB, int Day) FindSameDayPair(HarmonyBitmap bitmap, CellSymbol a, CellSymbol b, bool requireDifferentShift)
    {
        for (var day = 0; day < bitmap.DayCount; day++)
        {
            for (var rowA = 0; rowA < bitmap.RowCount; rowA++)
            {
                for (var rowB = 0; rowB < bitmap.RowCount; rowB++)
                {
                    var cellA = bitmap.GetCell(rowA, day);
                    var cellB = bitmap.GetCell(rowB, day);
                    if (rowA != rowB && cellA.Symbol == a && cellB.Symbol == b && cellA.WorkIds.Count == 1 && cellB.WorkIds.Count == 1
                        && (!requireDifferentShift || cellA.ShiftRefId != cellB.ShiftRefId))
                    {
                        return (rowA, rowB, day);
                    }
                }
            }
        }

        throw new InvalidOperationException("No matching pair in the fixture.");
    }
}
