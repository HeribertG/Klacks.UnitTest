// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Produces the plan the auction builds BEFORE any evolution happens — the same
/// <c>AuctionTokenStrategy</c> the population builder uses for half of the initial population.
/// <para>
/// This exists because of a real asymmetry in the engine: the auction actively steers towards the
/// 5-work/2-free rhythm and it is the only component that reads the previous month's runtime state,
/// while no fitness stage measures either afterwards. Running the same analyzer over the seed plan
/// and over the final plan therefore separates "the engine never built the pattern" from "the engine
/// built it and evolution dissolved it again" — a distinction no assertion on the final plan can make.
/// </para>
/// </summary>
public static class AutofillSeedPlanFactory
{
    /// <summary>Builds the auction seed plan with the scenario's own seed.</summary>
    /// <param name="definition">Scenario to seed from</param>
    public static CoreScenario BuildAuctionSeedPlan(AutofillScenarioDefinition definition)
        => new AuctionTokenStrategy().BuildScenario(definition.Context, new Random(definition.Config.RandomSeed));
}
