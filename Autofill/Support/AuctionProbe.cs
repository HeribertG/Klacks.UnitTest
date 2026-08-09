// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Common.Fuzzy;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Runs the production auction with the production controllers and a recording bidding agent, and
/// optionally with single rules removed from the rule base. The rule removal happens in an engine
/// instance built for this run only — the shipped <c>default-rules.json</c> is never touched — which
/// makes "what would this rule change do" a measurement instead of a hand calculation.
/// </summary>
public static class AuctionProbe
{
    /// <summary>The unmodified rule base.</summary>
    public static readonly IReadOnlyCollection<string> NoSuppressedRules = [];

    /// <param name="context">Engine input to auction</param>
    /// <param name="randomSeed">Seed of the scenario; the auction itself draws no random number</param>
    /// <param name="label">Name of this variant for the report</param>
    /// <param name="suppressedRules">Rule names to remove from the rule base before the run</param>
    public static AuctionProbeResult Run(
        CoreWizardContext context,
        int randomSeed,
        string label,
        IReadOnlyCollection<string> suppressedRules)
    {
        var rules = RuleBaseLoader.LoadDefault()
            .Where(rule => !suppressedRules.Contains(rule.Name))
            .ToList();

        var engine = new MamdaniInferenceEngine(
            DefaultLinguisticVariables.BuildInputs(),
            DefaultLinguisticVariables.BuildOutput(),
            rules);

        var recorder = new RecordingBiddingAgent(new FuzzyBiddingAgent(engine));
        var auctioneer = new SlotAuctioneer(
            recorder, new Stage0HardConstraintChecker(), new Stage1SoftConstraintChecker());

        var outcome = auctioneer.Run(context, new Random(randomSeed));
        return new AuctionProbeResult(label, outcome, recorder.Records);
    }
}
