// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// The rhythm of the touched rows as the second instance of the repair: among options of equal coverage,
/// legality and perturbation, the cover that fits the candidate's row wins - it extends a block of the same
/// shift kind instead of tearing a free block apart, mixing kinds or rotating backwards. Coverage, legality and
/// the minimal perturbation keep dominating.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineHarmonyTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void Cover_that_closes_the_candidates_block_beats_a_lonely_shift_with_lower_guid()
    {
        // Arrange: B (lower Guid) is free all around, C works early before and after the demand day.
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d.AddDays(-1), d, d.AddDays(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 7), At(d, 15), 8m)
            .Work(Agent(3), d.AddDays(-1), Shift(2), ShiftCategory.Early, At(d.AddDays(-1), 7), At(d.AddDays(-1), 15), 8m)
            .Work(Agent(3), d.AddDays(1), Shift(3), ShiftCategory.Early, At(d.AddDays(1), 7), At(d.AddDays(1), 15), 8m)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
        proposal.Objective.ShouldBe(new RecoveryObjective(0, 0, RulesetDefaults.WeightInGroupFree, 1));
        proposal.HarmonyDelta.ShouldBeLessThan(0);
    }

    [Test]
    public void Same_shift_kind_beats_a_mixed_block()
    {
        // Arrange: B (lower Guid) worked late the day before, C worked early.
        var d = Day(6, 3);
        var before = d.AddDays(-1);
        var snapshot = new SnapshotBuilder()
            .Days(before, d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 7), At(d, 15), 8m)
            .Work(Agent(2), before, Shift(2), ShiftCategory.Late, At(before, 7), At(before, 15), 8m)
            .Work(Agent(3), before, Shift(3), ShiftCategory.Early, At(before, 7), At(before, 15), 8m)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
        proposal.HarmonyDelta.ShouldBeLessThan(0);
    }

    [Test]
    public void Harmony_never_outranks_the_minimal_perturbation()
    {
        // Arrange: B is on call that day (cheaper tier) but would work a lonely shift; C would close its block.
        var d = Day(6, 3);
        var snapshot = new SnapshotBuilder()
            .Days(d.AddDays(-1), d, d.AddDays(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 7), At(d, 15), 8m)
            .Work(Agent(3), d.AddDays(-1), Shift(2), ShiftCategory.Early, At(d.AddDays(-1), 7), At(d.AddDays(-1), 15), 8m)
            .Work(Agent(3), d.AddDays(1), Shift(3), ShiftCategory.Early, At(d.AddDays(1), 7), At(d.AddDays(1), 15), 8m)
            .Availability(Agent(2), d, DayAvailability.AlwaysAvailable with { IsOnCall = true })
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(2));
        delta.Tier.ShouldBe(EscalationTier.InGroupOnCall);
        proposal.HarmonyDelta.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Harmony_ranks_before_the_shift_preference()
    {
        // Arrange: B prefers the shift but would work it alone; C would close its early block.
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d.AddDays(-1), d, d.AddDays(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", preferredShiftIds: [s])
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 7), At(d, 15), 8m)
            .Work(Agent(3), d.AddDays(-1), Shift(2), ShiftCategory.Early, At(d.AddDays(-1), 7), At(d.AddDays(-1), 15), 8m)
            .Work(Agent(3), d.AddDays(1), Shift(3), ShiftCategory.Early, At(d.AddDays(1), 7), At(d.AddDays(1), 15), 8m)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
    }

    [Test]
    public void Multi_day_absence_is_covered_as_one_block_by_the_fitting_agent()
    {
        // Arrange: A is absent for an early block of three days. B (lower Guid) is free all around; C works early
        // the day before and the day after, so taking the three days joins C's work into one early block.
        var days = new[] { Day(6, 3), Day(6, 4), Day(6, 5) };
        var first = Day(6, 2);
        var last = Day(6, 6);
        var builder = new SnapshotBuilder()
            .Days(first, days[0], days[1], days[2], last)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(3), first, Shift(8), ShiftCategory.Early, At(first, 7), At(first, 15), 8m)
            .Work(Agent(3), last, Shift(9), ShiftCategory.Early, At(last, 7), At(last, 15), 8m);
        for (var i = 0; i < days.Length; i++)
        {
            builder.Work(Agent(1), days[i], Shift(i + 1), ShiftCategory.Early, At(days[i], 7), At(days[i], 15), 8m);
        }

        // Act
        var proposal = _engine.Repair(builder.Build(), new AbsenceEvent(Agent(1), days), Ruleset.Default);

        // Assert
        proposal.Deltas.Count.ShouldBe(days.Length);
        proposal.Deltas.ShouldAllBe(x => x.ToAgentId == Agent(3));
    }
}
