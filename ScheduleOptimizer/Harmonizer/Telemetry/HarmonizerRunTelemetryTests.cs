// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Telemetry;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Telemetry;

[TestFixture]
public class HarmonizerRunTelemetryTests
{
    [Test]
    public void FitnessDelta_ComputesAfterMinusBefore()
    {
        var telemetry = BuildTelemetry(initial: 0.4, final: 0.7);

        telemetry.FitnessDelta.ShouldBe(0.3, 1e-9);
        telemetry.IsImprovement.ShouldBeTrue();
    }

    [Test]
    public void IsImprovement_FalseOnRegression()
    {
        var telemetry = BuildTelemetry(initial: 0.7, final: 0.4);

        telemetry.IsImprovement.ShouldBeFalse();
        telemetry.FitnessDelta.ShouldBeLessThan(0);
    }

    [Test]
    public void IsImprovement_FalseOnZeroDelta()
    {
        var telemetry = BuildTelemetry(initial: 0.5, final: 0.5);

        telemetry.IsImprovement.ShouldBeFalse();
    }

    private static HarmonizerRunTelemetry BuildTelemetry(double initial, double final)
    {
        return new HarmonizerRunTelemetry(
            JobId: Guid.NewGuid(),
            PeriodFrom: new DateOnly(2026, 1, 1),
            PeriodUntil: new DateOnly(2026, 1, 7),
            RowCount: 1,
            InitialFitness: initial,
            FinalFitness: final,
            EmergencyThreshold: 0.5,
            GenerationsRun: 5,
            TotalEmergencyUnlocks: 0,
            DurationMs: 1234,
            Rows: [new RowTelemetry("agent-0", 0, initial, final, 0, false)]);
    }
}
