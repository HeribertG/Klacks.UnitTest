// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Pins the rotation measuring instrument on hand-written plans, so a red rotation baseline later is
/// attributable to the engine and not to the analyzer. Three readings are checked side by side: the
/// SPEC.md decision-12b block compliance (inside a block the kind never falls, a restart across enough
/// rest is free), and the naive cyclic early-late-night-early reading in its start-to-start (engine
/// fitness) and last-to-first (what the plan shows) forms.
/// </summary>
[TestFixture]
public sealed class RotationMeasurementTests
{
    private const string Employee = "MA-01";

    private static readonly DateOnly Day1 = new(2026, 3, 1);
    private static readonly DateOnly Day2 = new(2026, 3, 2);
    private static readonly DateOnly Day3 = new(2026, 3, 3);
    private static readonly DateOnly Day4 = new(2026, 3, 4);
    private static readonly DateOnly Day5 = new(2026, 3, 5);
    private static readonly DateOnly Day7 = new(2026, 3, 7);

    [Test]
    public void AnAscendingPackageIsFullyCompliantAndHasNoPackagePair()
    {
        var metrics = Analyze(
            Token(Day1, AutofillShiftKind.Early),
            Token(Day2, AutofillShiftKind.Early),
            Token(Day3, AutofillShiftKind.Late),
            Token(Day4, AutofillShiftKind.Night));

        metrics.Rotation.BlockCompliance.PairCount.ShouldBe(3);
        metrics.Rotation.BlockCompliance.DescendingCount.ShouldBe(
            0, "Early, early, late, night stays or rises at every step, which 12b explicitly allows.");
        metrics.Rotation.BlockCompliance.CompliantRate.ShouldBe(1.0);
        metrics.Rotation.CyclicLastToFirst.PairCount.ShouldBe(0, "One package has no package pair.");
    }

    [Test]
    public void ANightFollowedByAnEarlyInsideOneBlockIsAViolationEvenAcrossAFreeCalendarDay()
    {
        var metrics = Analyze(
            Token(Day1, AutofillShiftKind.Late),
            Token(Day2, AutofillShiftKind.Night),
            Token(Day4, AutofillShiftKind.Early));

        var block = metrics.Rotation.BlockCompliance;
        block.PairCount.ShouldBe(
            2, "The night ends on day 3 at 07:00 and the early starts on day 4 at 07:00: 24 h is under the 48 h rest.");
        block.DescendingCount.ShouldBe(1, "Night to early inside one block falls, which 12b forbids.");
        block.CompliantRate.ShouldBe(0.5);

        metrics.Rotation.CyclicLastToFirst.ForwardCount.ShouldBe(
            1, "Read cyclically from the last kind, night to early is the rotation successor.");
        metrics.Rotation.CyclicStartToStart.BackwardCount.ShouldBe(
            1, "Read from the start kinds, as the engine fitness does, late to early is the cyclic predecessor.");
        metrics.Rotation.ForwardRate.ShouldBe(
            0, "The legacy package-pair rate compares start kinds and therefore misses the visible transition.");
    }

    [Test]
    public void ARestartAcrossEnoughRestOwesNoRotationUnder12bButIsStillCountedCyclically()
    {
        var metrics = Analyze(
            Token(Day1, AutofillShiftKind.Night),
            Token(Day5, AutofillShiftKind.Late));

        metrics.Rotation.BlockCompliance.PairCount.ShouldBe(
            0, "72 h of rest separate the two blocks, so the restart is free.");
        metrics.Rotation.BlockCompliance.CompliantRate.ShouldBe(
            1.0, "No in-block pair holds no violation; an empty set must not read as a rate of 0.");

        var cyclic = metrics.Rotation.CyclicLastToFirst;
        cyclic.PairCount.ShouldBe(1);
        cyclic.BackwardCount.ShouldBe(1, "Night to late is the cyclic predecessor.");
        cyclic.ForwardRate.ShouldBe(0);
    }

    private static Klacks.UnitTest.Autofill.Analysis.Model.AutofillMetrics Analyze(params CoreToken[] tokens)
    {
        var definition = new AutofillScenarioBuilder()
            .WithPeriod(Day1, Day7)
            .AddEmployee(Employee, AutofillSpecConstants.GuaranteedHours)
            .Build();

        return AutofillPlanAnalyzer.Analyze(
            new CoreScenario { Tokens = [.. tokens] }, definition, "test", "test", "run1");
    }

    private static CoreToken Token(DateOnly date, AutofillShiftKind kind)
    {
        var (startAt, endAt) = AutofillShiftCatalog.SpanOf(kind, date);
        return new CoreToken(
            WorkIds: [$"{Employee}-{date:yyyyMMdd}-{kind}"],
            ShiftTypeIndex: AutofillShiftCatalog.ShiftTypeIndexOf(kind),
            Date: date,
            TotalHours: (decimal)AutofillSpecConstants.ShiftHours,
            StartAt: startAt,
            EndAt: endAt,
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: AutofillShiftCatalog.LocationContextOf(AutofillShiftCatalog.SingleOrderIndex),
            ShiftRefId: AutofillShiftCatalog.ShiftIdOf(AutofillShiftCatalog.SingleOrderIndex, kind),
            AgentId: Employee);
    }
}
