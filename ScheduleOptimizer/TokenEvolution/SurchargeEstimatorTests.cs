// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

[TestFixture]
public sealed class SurchargeEstimatorTests
{
    private const int NightShiftTypeIndex = 2;
    private const int DayShiftTypeIndex = 0;

    private static readonly DateOnly Wednesday = new(2026, 4, 22);
    private static readonly DateOnly Saturday = new(2026, 4, 25);
    private static readonly DateOnly Sunday = new(2026, 4, 26);

    private static CoreAgent MakeAgent() => new(
        Id: "A",
        CurrentHours: 0,
        GuaranteedHours: 160,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2);

    [Test]
    public void Estimate_NullAgent_ReturnsZero()
    {
        SurchargeEstimator.Estimate(8m, NightShiftTypeIndex, Wednesday, null).ShouldBe(0m);
    }

    [Test]
    public void Estimate_NonPositiveHours_ReturnsZero()
    {
        var agent = MakeAgent() with { NightRate = 0.25m };

        SurchargeEstimator.Estimate(0m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(0m);
        SurchargeEstimator.Estimate(-4m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(0m);
    }

    [Test]
    public void Estimate_ZeroRate_ReturnsZero()
    {
        var agent = MakeAgent() with { NightRate = 0m, NightRateMode = CoreSurchargeRateMode.FixedPerShift };

        SurchargeEstimator.Estimate(8m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(0m);
    }

    [Test]
    public void Estimate_NoMatchingSurchargeType_ReturnsZero()
    {
        var agent = MakeAgent() with { NightRate = 0.25m, WE1Rate = 0.5m, WE2Rate = 0.5m };

        SurchargeEstimator.Estimate(8m, DayShiftTypeIndex, Wednesday, agent).ShouldBe(0m);
    }

    [Test]
    public void Estimate_MultiplierMode_IsHoursTimesRate()
    {
        var agent = MakeAgent() with { NightRate = 0.25m };

        SurchargeEstimator.Estimate(8m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(2m);
    }

    [Test]
    public void Estimate_MultiplierMode_IsTheDefaultWhenNoModeIsSet()
    {
        var agent = MakeAgent() with { WE2Rate = 0.5m };

        agent.WE2RateMode.ShouldBe(CoreSurchargeRateMode.Multiplier);
        SurchargeEstimator.Estimate(6m, DayShiftTypeIndex, Sunday, agent).ShouldBe(3m);
    }

    [Test]
    public void Estimate_MultiplierMode_AddsNightAndWeekendRates()
    {
        var agent = MakeAgent() with { NightRate = 0.25m, WE1Rate = 0.5m };

        SurchargeEstimator.Estimate(8m, NightShiftTypeIndex, Saturday, agent).ShouldBe(6m);
    }

    [Test]
    public void Estimate_FixedPerHourMode_ScalesWithHours()
    {
        var agent = MakeAgent() with { NightRate = 5m, NightRateMode = CoreSurchargeRateMode.FixedPerHour };

        SurchargeEstimator.Estimate(4m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(20m);
        SurchargeEstimator.Estimate(12m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(60m);
    }

    [Test]
    public void Estimate_FixedPerShiftMode_IsIndependentOfHours()
    {
        var agent = MakeAgent() with { NightRate = 30m, NightRateMode = CoreSurchargeRateMode.FixedPerShift };

        SurchargeEstimator.Estimate(4m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(30m);
        SurchargeEstimator.Estimate(12m, NightShiftTypeIndex, Wednesday, agent).ShouldBe(30m);
    }

    [Test]
    public void Estimate_FixedPerShiftMode_AppliesToWeekendRatesToo()
    {
        var agent = MakeAgent() with { WE2Rate = 20m, WE2RateMode = CoreSurchargeRateMode.FixedPerShift };

        SurchargeEstimator.Estimate(9m, DayShiftTypeIndex, Sunday, agent).ShouldBe(20m);
    }

    [Test]
    public void Estimate_MixedModes_CombinesEachRateByItsOwnMode()
    {
        var agent = MakeAgent() with
        {
            NightRate = 30m,
            NightRateMode = CoreSurchargeRateMode.FixedPerShift,
            WE1Rate = 0.5m,
            WE1RateMode = CoreSurchargeRateMode.Multiplier,
        };

        SurchargeEstimator.Estimate(8m, NightShiftTypeIndex, Saturday, agent).ShouldBe(34m);
    }

    [Test]
    public void Estimate_SaturdayUsesWE1AndSundayUsesWE2()
    {
        var agent = MakeAgent() with { WE1Rate = 0.5m, WE2Rate = 1m };

        SurchargeEstimator.Estimate(8m, DayShiftTypeIndex, Saturday, agent).ShouldBe(4m);
        SurchargeEstimator.Estimate(8m, DayShiftTypeIndex, Sunday, agent).ShouldBe(8m);
    }
}
