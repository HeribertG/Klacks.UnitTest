// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

[TestFixture]
public class TokenEvolutionLoopWheelRunTests
{
    private const int SmallPopulation = 8;
    private const int FewGenerations = 15;

    [Test]
    public void NoWheel_RunIsIdenticalToTheLoopWithoutWheel()
    {
        var definition = CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours);
        RotationWheel.Build(definition.Context).ShouldBeNull();

        var withWheelLoop = TokenEvolutionLoop.Create().Run(definition.Context, definition.Config);
        var withoutWheelLoop = LoopWithoutWheel().Run(definition.Context, definition.Config);

        PlanFingerprint.ForScenario(withWheelLoop).ShouldBe(PlanFingerprint.ForScenario(withoutWheelLoop));
    }

    [Test]
    public void Wheel_JudgeNeverRatesTheResultBelowTheFirstRun()
    {
        var definition = CleanStart(AutofillSpecConstants.GuaranteedHours);
        RotationWheel.Build(definition.Context).ShouldNotBeNull();

        var chosen = TokenEvolutionLoop.Create().Run(definition.Context, definition.Config);
        var firstRun = LoopWithoutWheel().Run(definition.Context, definition.Config);

        var evaluator = TokenFitnessEvaluator.Create(definition.Context, definition.Config);
        evaluator.Evaluate(chosen, definition.Context);
        evaluator.Evaluate(firstRun, definition.Context);
        evaluator.Compare(chosen, firstRun).ShouldBeLessThanOrEqualTo(0);
    }

    [Test]
    public void Wheel_CleanStartMarch_TheWheelRunWins()
    {
        var definition = CleanStart(AutofillSpecConstants.GuaranteedHours);

        var chosen = TokenEvolutionLoop.Create().Run(definition.Context, definition.Config);
        var firstRun = LoopWithoutWheel().Run(definition.Context, definition.Config);

        var evaluator = TokenFitnessEvaluator.Create(definition.Context, definition.Config);
        evaluator.Evaluate(chosen, definition.Context);
        evaluator.Evaluate(firstRun, definition.Context);
        evaluator.Compare(chosen, firstRun).ShouldBeLessThan(0);
    }

    [Test]
    public void Wheel_CancelledDuringTheWheelRun_TheFirstRunsPlanStays()
    {
        var definition = CleanStart(AutofillSpecConstants.GuaranteedHours);
        using var cancellation = new CancellationTokenSource();

        var chosen = TokenEvolutionLoop.Create().Run(
            definition.Context,
            definition.Config,
            cancellationToken: cancellation.Token,
            trace: line =>
            {
                if (line.StartsWith("[wheel] Run: enter", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
            });
        var firstRun = LoopWithoutWheel().Run(definition.Context, definition.Config);

        cancellation.IsCancellationRequested.ShouldBeTrue();
        PlanFingerprint.ForScenario(chosen).ShouldBe(PlanFingerprint.ForScenario(firstRun));
    }

    [Test]
    public void Wheel_TinyTimeBudget_SecondRunIsSkippedAndTheFirstRunStays()
    {
        var definition = CleanStart(AutofillSpecConstants.GuaranteedHours);
        var config = definition.Config with { MaxRuntime = TimeSpan.FromTicks(1) };

        var chosen = TokenEvolutionLoop.Create().Run(definition.Context, config);
        var firstRun = LoopWithoutWheel().Run(definition.Context, config);

        PlanFingerprint.ForScenario(chosen).ShouldBe(PlanFingerprint.ForScenario(firstRun));
    }

    private static TokenEvolutionLoop LoopWithoutWheel()
        => new(
            new TokenPopulationBuilder(
                new AuctionTokenStrategy(),
                new CoverageFirstTokenStrategy(),
                new GreedyTokenStrategy(),
                new RandomTokenStrategy(),
                new WarmStartTokenStrategy(),
                new RotationTemplateTokenStrategy()),
            new BlockCrossover(),
            new TokenSwapMutation(),
            new BlockSplitMutation(),
            new BlockMergeMutation(),
            new ReassignMutation(),
            new TokenRepair(new TokenConstraintChecker()));

    private static AutofillScenarioDefinition CleanStart(double hours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, hours)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(SmallPopulation, FewGenerations)
            .Build();
}
