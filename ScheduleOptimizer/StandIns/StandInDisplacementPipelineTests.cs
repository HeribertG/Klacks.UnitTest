// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.StandIns;

/// <summary>
/// Owner rule for placeholders (stand-ins): they fill only what real people cannot, real people are never displaced.
/// Runs the three AutoWizard stages on the clip-S1 stage (<see cref="FairStage"/>) wired like production - Wizard 1 as
/// the TokenEvolutionLoop, Wizard 2 as the conductor-only harmonizer run of HarmonizerJobRunner (harmony guard on,
/// regression gate), Wizard 3 as the deterministic engine - and counts the places held by real people per stage.
/// </summary>
[TestFixture]
public class StandInDisplacementPipelineTests
{
    private const double EmergencyThreshold = 0.5;
    private const int HarmonizerSeed = 7;

    private static readonly object[] Cases =
    [
        new object[] { 1, 0d },
        new object[] { 2, 0d },
        new object[] { 3, 0d },
        new object[] { 1, 160d },
        new object[] { 2, 160d },
        new object[] { 3, 160d },
    ];

    [TestCaseSource(nameof(Cases))]
    public void Wizard1_WithoutStandIns_RealPeopleFillEveryPlaceTheyCan(int seed, double regularCurrentHours)
    {
        var context = FairStage.Context(withStandIns: false, regularCurrentHours);

        var plan = RunWizard1(context, seed);

        TestContext.Out.WriteLine($"W1 without stand-ins (seed {seed}, regulars {regularCurrentHours} h): real {FairStage.RealPlaces(plan)} of {FairStage.Places}");
        FairStage.RealPlaces(plan).ShouldBe(FairStage.RealCapacity);
    }

    [TestCaseSource(nameof(Cases))]
    public void Wizard1_WithStandIns_RealPeopleStillFillEveryPlaceTheyCan(int seed, double regularCurrentHours)
    {
        var context = FairStage.Context(withStandIns: true, regularCurrentHours);

        var plan = RunWizard1(context, seed);

        TestContext.Out.WriteLine($"W1 with stand-ins (seed {seed}, regulars {regularCurrentHours} h): real {FairStage.RealPlaces(plan)}, stand-ins {FairStage.StandInPlaces(plan)}, filled {plan.Tokens.Count} of {FairStage.Places}");
        plan.Tokens.Count.ShouldBe(FairStage.Places);
        FairStage.RealPlaces(plan).ShouldBe(FairStage.RealCapacity);
    }

    [TestCaseSource(nameof(Cases))]
    public void Wizard2_HarmonizerNeverHandsAPlaceOfARealPersonToAStandIn(int seed, double regularCurrentHours)
    {
        var context = FairStage.Context(withStandIns: true, regularCurrentHours);
        var input = FairStage.BitmapOf(RunWizard1(context, seed), context);
        var start = RowSorter.Sort(BitmapBuilder.Build(input));
        var before = FairStage.StandInCells(start);

        var harmonized = RunHarmonizer(input, start);

        var after = FairStage.StandInCells(harmonized);
        TestContext.Out.WriteLine($"W2 (seed {seed}, regulars {regularCurrentHours} h): stand-in places {before} -> {after}");
        after.ShouldBeLessThanOrEqualTo(before);
    }

    [TestCaseSource(nameof(Cases))]
    public void Wizard3_DeterministicSearchNeverHandsAPlaceOfARealPersonToAStandIn(int seed, double regularCurrentHours)
    {
        var context = FairStage.Context(withStandIns: true, regularCurrentHours);
        var input = FairStage.BitmapOf(RunWizard1(context, seed), context);
        var working = RowSorter.Sort(BitmapBuilder.Build(input));
        var before = FairStage.StandInCells(working);

        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, HolisticHarmonizerComponents.UntrimmedPool);
        new DeterministicHarmonyOptimizer(components, DeterministicSearchOptions.Harmonized).Run(working, null, CancellationToken.None);

        var after = FairStage.StandInCells(working);
        TestContext.Out.WriteLine($"W3 (seed {seed}, regulars {regularCurrentHours} h): stand-in places {before} -> {after}");
        after.ShouldBeLessThanOrEqualTo(before);
    }

    [TestCase(1)]
    [TestCase(2)]
    public void GroupWithoutRealPeople_EveryStageFillsEveryPlaceWithStandIns(int seed)
    {
        var context = FairStage.StandInsOnlyContext();

        var plan = RunWizard1(context, seed);
        var input = FairStage.BitmapOf(plan, context);
        var harmonized = RunHarmonizer(input, RowSorter.Sort(BitmapBuilder.Build(input)));
        var polished = BitmapCloner.Clone(harmonized);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, HolisticHarmonizerComponents.UntrimmedPool);
        new DeterministicHarmonyOptimizer(components, DeterministicSearchOptions.Harmonized).Run(polished, null, CancellationToken.None);

        TestContext.Out.WriteLine($"Stand-ins only (seed {seed}): W1 {plan.Tokens.Count}, W2 {FairStage.StandInCells(harmonized)}, W3 {FairStage.StandInCells(polished)} of {FairStage.StandInsOnlyPlaces}");
        plan.FitnessStage0.ShouldBe(0);
        plan.Tokens.Count.ShouldBe(FairStage.StandInsOnlyPlaces);
        FairStage.StandInCells(harmonized).ShouldBe(FairStage.StandInsOnlyPlaces);
        FairStage.StandInCells(polished).ShouldBe(FairStage.StandInsOnlyPlaces);
    }

    private static CoreScenario RunWizard1(CoreWizardContext context, int seed)
    {
        var config = new TokenEvolutionConfig
        {
            RandomSeed = seed,
            EvaluationParallelism = 1,
        };

        return TokenEvolutionLoop.Create().Run(context, config);
    }

    private static HarmonyBitmap RunHarmonizer(BitmapInput input, HarmonyBitmap start)
    {
        var original = BitmapCloner.Clone(start);
        var scorer = new HarmonyScorer();
        IReplaceValidator validator = DomainAwareReplaceValidator.ForInput(input, BitmapRuleRuntime.TryCreate(input));
        validator = HarmonyGuardReplaceValidator.ForStart(validator, start);
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var config = new HarmonizerEvolutionConfig(PopulationSize: 2, MaxGenerations: 0, Seed: HarmonizerSeed);

        HarmonizerConductor BuildConductor(int rowCount) => new(
            scorer,
            new ReplaceMutation(scorer, validator),
            new EmergencyUnlockManager(new EmergencyUnlockState(rowCount), EmergencyThreshold),
            hints: input.SofteningHints,
            blockSwapMutation: new BlockSwapMutation(scorer, validator));

        var loop = new HarmonizerEvolutionLoop(fitness, new StochasticBitmapMutation(validator), BuildConductor, config);
        var initialFitness = fitness.Evaluate(start).Fitness;
        var best = loop.Run(start).Best;
        return best.Fitness < initialFitness ? original : best.Bitmap;
    }
}
