// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WarmStartTrainingOverridesTests
{
    [Test]
    public void Apply_InitWarmStartRatioWithinRange_IsApplied()
    {
        var baseline = new TokenEvolutionConfig();
        var overrides = new WizardTrainingOverrides(InitWarmStartRatio: 0.35);

        var result = overrides.Apply(baseline);

        result.InitWarmStartRatio.ShouldBe(0.35);
    }

    [Test]
    public void Apply_InitWarmStartRatioAboveMax_IsClampedToUpperBound()
    {
        var baseline = new TokenEvolutionConfig();
        var overrides = new WizardTrainingOverrides(InitWarmStartRatio: 0.9);

        var result = overrides.Apply(baseline);

        result.InitWarmStartRatio.ShouldBe(0.4);
    }

    [Test]
    public void Apply_InitWarmStartRatioBelowZero_IsClampedToZero()
    {
        var baseline = new TokenEvolutionConfig();
        var overrides = new WizardTrainingOverrides(InitWarmStartRatio: -0.1);

        var result = overrides.Apply(baseline);

        result.InitWarmStartRatio.ShouldBe(0.0);
    }

    [Test]
    public void Apply_InitWarmStartRatioNull_KeepsBaseline()
    {
        var baseline = new TokenEvolutionConfig { InitWarmStartRatio = 0.25 };
        var overrides = new WizardTrainingOverrides(InitWarmStartRatio: null);

        var result = overrides.Apply(baseline);

        result.InitWarmStartRatio.ShouldBe(0.25);
    }
}
