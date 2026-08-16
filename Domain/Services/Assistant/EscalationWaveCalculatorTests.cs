// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pure arithmetic tests for EscalationWaveCalculator - the B3 rolling per-stage deadline and the
/// B4 parallel-below-floor switch (docs/ENTWURF-eskalationskette-2026-08-16.md §5/§6), including the
/// exact numbers from the Entwurf's own reference case and parallel probe.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class EscalationWaveCalculatorTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ReferenceCase_SixtyMinutesThreeStages_TwentyMinuteSerialStage()
    {
        var deadline = Now.AddMinutes(60);

        var wave = EscalationWaveCalculator.ComputeNextWave(Now, deadline, pendingStageCount: 3, minStageMinutes: 5, maxStageMinutes: 30);

        Assert.That(wave.IsParallel, Is.False);
        Assert.That(wave.StageCount, Is.EqualTo(1));
        Assert.That(wave.Duration, Is.EqualTo(TimeSpan.FromMinutes(20)));
    }

    [Test]
    public void ReferenceCase_AfterFirstExpiry_FortyMinutesTwoStages_TwentyMinuteSerialStage()
    {
        var now = Now.AddMinutes(20);
        var deadline = Now.AddMinutes(60);

        var wave = EscalationWaveCalculator.ComputeNextWave(now, deadline, pendingStageCount: 2, minStageMinutes: 5, maxStageMinutes: 30);

        Assert.That(wave.IsParallel, Is.False);
        Assert.That(wave.Duration, Is.EqualTo(TimeSpan.FromMinutes(20)));
    }

    [Test]
    public void ParallelProbe_TenMinutesThreeStages_SwitchesToParallelAtTheFloor()
    {
        var deadline = Now.AddMinutes(10);

        var wave = EscalationWaveCalculator.ComputeNextWave(Now, deadline, pendingStageCount: 3, minStageMinutes: 5, maxStageMinutes: 30);

        Assert.That(wave.IsParallel, Is.True);
        Assert.That(wave.StageCount, Is.EqualTo(3));
        Assert.That(wave.Duration, Is.EqualTo(TimeSpan.FromMinutes(5)));
    }

    [Test]
    public void DecisionB4_DeadlineAlreadyPassed_AllStagesParallelAtTheMinimum()
    {
        var deadline = Now.AddMinutes(-15);

        var wave = EscalationWaveCalculator.ComputeNextWave(Now, deadline, pendingStageCount: 3, minStageMinutes: 5, maxStageMinutes: 30);

        Assert.That(wave.IsParallel, Is.True);
        Assert.That(wave.StageCount, Is.EqualTo(3));
        Assert.That(wave.Duration, Is.EqualTo(TimeSpan.FromMinutes(5)));
    }

    [Test]
    public void PerStageDurationIsClampedAtTheCeiling()
    {
        var deadline = Now.AddHours(10);

        var wave = EscalationWaveCalculator.ComputeNextWave(Now, deadline, pendingStageCount: 1, minStageMinutes: 5, maxStageMinutes: 30);

        Assert.That(wave.IsParallel, Is.False);
        Assert.That(wave.Duration, Is.EqualTo(TimeSpan.FromMinutes(30)));
    }

    [Test]
    public void NoPendingStages_YieldsAnEmptyWave()
    {
        var wave = EscalationWaveCalculator.ComputeNextWave(Now, Now.AddHours(1), pendingStageCount: 0, minStageMinutes: 5, maxStageMinutes: 30);

        Assert.That(wave.StageCount, Is.EqualTo(0));
    }
}
