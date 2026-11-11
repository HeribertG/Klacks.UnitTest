// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// The softening hints production hands Wizard 2 after a Wizard 1 run: WizardJobRunner.BuildAuctionTelemetry runs the
/// auction once more with the run's seed, the apply persists its escalations for the employees holding a placement and
/// for days of the period, and the harmonizer context reloads them as hints. The conductor reads only which agents carry
/// a hint (a doubled iteration budget for their rows), so every hint here carries <see cref="SofteningKind.Unknown"/>
/// and one hint stands for one (agent, day).
/// </summary>
public static class ChainSofteningHints
{
    /// <param name="definition">Scenario Wizard 1 ran on</param>
    /// <param name="wizard1">Plan Wizard 1 produced</param>
    public static IReadOnlyList<SofteningHint> Build(AutofillScenarioDefinition definition, CoreScenario wizard1)
    {
        var outcome = new SlotAuctioneer(
                new FuzzyBiddingAgent(), new Stage0HardConstraintChecker(), new Stage1SoftConstraintChecker())
            .Run(definition.Context, new Random(definition.Config.RandomSeed));
        var planned = wizard1.Tokens.Select(t => t.AgentId).ToHashSet(StringComparer.Ordinal);

        return outcome.Escalation.Entries
            .Where(e => planned.Contains(e.AgentId))
            .Where(e => e.Date >= definition.PeriodFrom && e.Date <= definition.PeriodUntil)
            .GroupBy(e => (e.AgentId, e.Date))
            .OrderBy(g => g.Key.AgentId, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Date)
            .Select(g => new SofteningHint(
                g.Key.AgentId,
                g.Key.Date,
                SofteningKind.Unknown,
                g.Select(e => e.RuleName).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
