// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario3;
using Klacks.UnitTest.Autofill.Scenarios.Scenario4;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The balancer prices a swap's shift-kind fairness from the kind counts before it builds the swapped plan. That
/// shortcut may only skip the plan if it yields the very value the evaluator computes on the built plan, to the bit:
/// otherwise a candidate on the edge of the strict "fairness rises" test would be judged differently and the
/// balancer, and with it every Wizard 1 run, would take another path.
/// </summary>
[TestFixture]
public sealed class ShiftKindBalancerExchangeFairnessTests
{
    private const int MinimumComparedSwaps = 5;

    private static IEnumerable<TestCaseData> Plans()
    {
        yield return new TestCaseData((Func<AutofillScenarioDefinition>)Scenario3EligibilityFixture.BuildL1)
            .SetName("ScenarioThreeWithBans");
        yield return new TestCaseData((Func<AutofillScenarioDefinition>)Scenario4CarryInFixture.BuildMainRun)
            .SetName("ScenarioFourWithOrders");
        yield return new TestCaseData((Func<AutofillScenarioDefinition>)(() => AutofillScaleFixture.Build(
                AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations)))
            .SetName("FiftyEmployeesTenSeats");
    }

    [TestCaseSource(nameof(Plans))]
    public void FairnessAfterExchange_EqualsTheFairnessOfTheBuiltSwapToTheBit(Func<AutofillScenarioDefinition> build)
    {
        var definition = build();
        var context = definition.Context;
        var evaluator = TokenFitnessEvaluator.Create(context, definition.Config);
        var plan = AutofillSeedPlanFactory.BuildAuctionSeedPlan(definition);
        var counts = TokenFitnessEvaluator.CountShiftKinds(plan.Tokens);
        var countsBefore = new Dictionary<(int Kind, string AgentId), int>(counts);
        var compared = 0;

        foreach (var (agentA, blockA, agentB, blockB) in BlockPairs(plan, context))
        {
            var overlap = KindBlockSwap.SharedDays(blockA, blockB);
            if (overlap is null)
            {
                continue;
            }

            var built = KindBlockSwap.TrySwap(plan, context, agentA, overlap.FromA, agentB, overlap.FromB);
            if (built is null)
            {
                continue;
            }

            var fromCounts = ShiftKindBalancer.FairnessAfterExchange(
                counts, agentA.Id, blockA.Kind, agentB.Id, blockB.Kind, overlap.FromA.Count, context, evaluator);

            BitConverter.DoubleToInt64Bits(fromCounts).ShouldBe(
                BitConverter.DoubleToInt64Bits(evaluator.ComputeShiftKindFairnessScore(built, context)));
            compared++;
        }

        compared.ShouldBeGreaterThanOrEqualTo(MinimumComparedSwaps);
        counts.Where(entry => entry.Value != 0).ShouldBe(
            countsBefore.Where(entry => entry.Value != 0), ignoreOrder: true);
    }

    private static IEnumerable<(CoreAgent AgentA, KindBlock BlockA, CoreAgent AgentB, KindBlock BlockB)> BlockPairs(
        CoreScenario plan, CoreWizardContext context)
    {
        var blocks = context.Agents.ToDictionary(
            agent => agent.Id, agent => KindBlockSwap.BuildKindBlocks(plan.Tokens, agent.Id, context));
        for (var a = 0; a < context.Agents.Count; a++)
        {
            for (var b = a + 1; b < context.Agents.Count; b++)
            {
                foreach (var blockA in blocks[context.Agents[a].Id])
                {
                    foreach (var blockB in blocks[context.Agents[b].Id])
                    {
                        yield return (context.Agents[a], blockA, context.Agents[b], blockB);
                    }
                }
            }
        }
    }
}
