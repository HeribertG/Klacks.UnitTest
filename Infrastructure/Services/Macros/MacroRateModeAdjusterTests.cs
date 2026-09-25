// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroRateModeAdjuster: FixedPerShift replaces a surcharge amount by the flat rate and moves the result
/// value by the same delta, a Multiplier minimum per hour raises an amount below the floor, and a failed result or a
/// result without surcharge items passes through unchanged.
/// </summary>

using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroRateModeAdjusterTests
{
    [Test]
    public void FixedPerShift_ReplacesTheAmountByTheFlatRate_AndMovesTheResultByTheSameDelta()
    {
        var data = new MacroData { NightRateMode = SurchargeRateMode.FixedPerShift, NightRate = 20m };
        var result = new MacroExecutionResult(true, 10m, [new MacroSurchargeItem(SurchargeType.Night, 8m)]);

        var adjusted = MacroRateModeAdjuster.Apply(result, data);

        adjusted.Surcharges.Single().Amount.ShouldBe(20m);
        adjusted.ResultValue.ShouldBe(22m);
    }

    [Test]
    public void MultiplierWithMinimumPerHour_UsesTheFloorWhenHigher()
    {
        var data = new MacroData
        {
            NightRateMode = SurchargeRateMode.Multiplier,
            NightRate = 0.25m,
            NightMinimumPerHour = 0.5m
        };
        var result = new MacroExecutionResult(true, 2m, [new MacroSurchargeItem(SurchargeType.Night, 2m)]);

        var adjusted = MacroRateModeAdjuster.Apply(result, data);

        adjusted.Surcharges.Single().Amount.ShouldBe(4m);
        adjusted.ResultValue.ShouldBe(4m);
    }

    [Test]
    public void FailedResultOrResultWithoutSurcharges_PassesThroughUnchanged()
    {
        var data = new MacroData { NightRateMode = SurchargeRateMode.FixedPerShift, NightRate = 20m };
        var failed = new MacroExecutionResult(false, null);
        var plain = new MacroExecutionResult(true, 3m);

        MacroRateModeAdjuster.Apply(failed, data).ShouldBeSameAs(failed);
        MacroRateModeAdjuster.Apply(plain, data).ShouldBeSameAs(plain);
    }
}
