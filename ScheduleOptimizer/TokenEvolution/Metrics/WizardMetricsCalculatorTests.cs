// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Metrics;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

[TestFixture]
public sealed class WizardMetricsCalculatorTests
{
    private static CoreAgent MakeAgent(string id, double guaranteedHours) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: guaranteedHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = guaranteedHours,
        PerformsShiftWork = true,
    };

    private static CoreToken MakeToken(string agentId, DateOnly date, decimal hours) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date,
        TotalHours: hours,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.NewGuid(),
        AgentId: agentId);

    private static CoreWizardContext MakeContext(params CoreAgent[] agents) => new()
    {
        PeriodFrom = new DateOnly(2026, 4, 20),
        PeriodUntil = new DateOnly(2026, 4, 26),
        Agents = agents,
        Shifts = [],
    };

    private static CoreScenario MakeScenario(params CoreToken[] tokens) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Tokens = tokens.ToList(),
    };

    [Test]
    public void RosterFidelity_TopMoreAccurateThanBottom_IsZero()
    {
        var ctx = MakeContext(MakeAgent("top", 40), MakeAgent("mid", 40), MakeAgent("bottom", 40));
        var d = new DateOnly(2026, 4, 20);
        var scenario = MakeScenario(
            MakeToken("top", d, 40),
            MakeToken("mid", d, 32),
            MakeToken("bottom", d, 16));

        var snapshot = WizardMetricsCalculator.Compute(scenario, ctx, 0);

        snapshot.RosterFidelityInversionRate.ShouldBe(0.0);
    }

    [Test]
    public void RosterFidelity_TopStarvedWhileBottomExact_IsFullyInverted()
    {
        var ctx = MakeContext(MakeAgent("top", 40), MakeAgent("bottom", 40));
        var d = new DateOnly(2026, 4, 20);
        var scenario = MakeScenario(MakeToken("bottom", d, 40));

        var snapshot = WizardMetricsCalculator.Compute(scenario, ctx, 0);

        snapshot.RosterFidelityInversionRate.ShouldBe(1.0);
    }

    [Test]
    public void RosterFidelity_NearlyEqualDeviations_AreNotCountedAsInversion()
    {
        var ctx = MakeContext(MakeAgent("top", 100), MakeAgent("bottom", 100));
        var d = new DateOnly(2026, 4, 20);

        // 90.2 vs 90.5 of 100: deviation gap 0.003 lies below the 0.01 epsilon.
        var scenario = MakeScenario(
            MakeToken("top", d, 90.2m),
            MakeToken("bottom", d, 90.5m));

        var snapshot = WizardMetricsCalculator.Compute(scenario, ctx, 0);

        snapshot.RosterFidelityInversionRate.ShouldBe(0.0);
    }

    [Test]
    public void RosterFidelity_OvershootCountsAsInaccuracy()
    {
        var ctx = MakeContext(MakeAgent("top", 40), MakeAgent("bottom", 40));
        var d = new DateOnly(2026, 4, 20);

        // Top overshoots by 25%, bottom hits the target exactly — that is an inversion.
        var scenario = MakeScenario(
            MakeToken("top", d, 50),
            MakeToken("bottom", d, 40));

        var snapshot = WizardMetricsCalculator.Compute(scenario, ctx, 0);

        snapshot.RosterFidelityInversionRate.ShouldBe(1.0);
    }

    [Test]
    public void RosterFidelity_AgentsWithoutGuaranteedHours_AreIgnored()
    {
        var ctx = MakeContext(MakeAgent("top", 0), MakeAgent("only", 40));
        var d = new DateOnly(2026, 4, 20);
        var scenario = MakeScenario(MakeToken("only", d, 40));

        var snapshot = WizardMetricsCalculator.Compute(scenario, ctx, 0);

        snapshot.RosterFidelityInversionRate.ShouldBe(0.0);
    }
}
