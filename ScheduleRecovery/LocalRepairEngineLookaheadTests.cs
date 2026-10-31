// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Lookahead over the whole absence (<see cref="Ruleset.LookaheadBeamWidth"/>): the greedy repair covers one demand
/// after the other and can spend the one agent a later demand needs. The lookahead plans the absence as a whole and
/// replaces the greedy result only when it is strictly better in the objective order, so it is never worse.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineLookaheadTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void Lookahead_keeps_a_two_day_absence_in_the_group_where_greedy_has_to_borrow()
    {
        // Arrange: A is absent on d1 and d2. B is on call on d1 and has worked the five days before it, so with
        // MaxConsecutiveDays 6 B can take d1 or d2 but not both. C is free on d1 and holds its own early shift on d2.
        // X belongs to another group. Greedy takes the cheaper on-call B for d1 and must then borrow X for d2.
        var (snapshot, absence) = BuildTwoDayAbsence();

        // Act
        var greedy = _engine.Repair(snapshot, absence, Ruleset.Default with { LookaheadBeamWidth = 1 });
        var lookahead = _engine.Repair(snapshot, absence, Ruleset.Default);

        // Assert
        greedy.Objective.Perturbation.ShouldBe(RulesetDefaults.WeightInGroupOnCall + RulesetDefaults.WeightCrossGroupFree);
        greedy.Deltas.ShouldContain(d => d.ToAgentId == Agent(4) && d.Tier == EscalationTier.CrossGroupFree);

        lookahead.Objective.UncoveredCritical.ShouldBe(0);
        lookahead.Objective.Perturbation.ShouldBe(2 * RulesetDefaults.WeightInGroupFree);
        lookahead.Deltas.Select(d => (d.Date, d.ToAgentId))
            .ShouldBe([(Day(6, 8), Agent(3)), (Day(6, 9), Agent(2))]);
        lookahead.MembershipDeltas.ShouldBeEmpty();
    }

    [Test]
    public void Lookahead_is_deterministic()
    {
        // Arrange
        var (snapshot, absence) = BuildTwoDayAbsence();

        // Act
        var first = Format(_engine.Repair(snapshot, absence, Ruleset.Default));
        var second = Format(_engine.Repair(snapshot, absence, Ruleset.Default));

        // Assert
        second.ShouldBe(first);
    }

    [Test]
    public void A_single_demand_is_repaired_exactly_like_the_greedy_repair()
    {
        // Arrange: one demand leaves nothing to look ahead to.
        var d = Day(6, 3);
        var snapshot = new SnapshotBuilder()
            .Days(d.AddDays(-1), d, d.AddDays(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, Shift(1), ShiftCategory.Early, At(d, 7), At(d, 15), 8m)
            .Work(Agent(3), d.AddDays(-1), Shift(2), ShiftCategory.Early, At(d.AddDays(-1), 7), At(d.AddDays(-1), 15), 8m)
            .Build();
        var absence = new AbsenceEvent(Agent(1), [d]);

        // Act
        var greedy = Format(_engine.Repair(snapshot, absence, Ruleset.Default with { LookaheadBeamWidth = 1 }));
        var lookahead = Format(_engine.Repair(snapshot, absence, Ruleset.Default));

        // Assert
        lookahead.ShouldBe(greedy);
    }

    private static (RecoverySnapshot Snapshot, AbsenceEvent Absence) BuildTwoDayAbsence()
    {
        var d1 = Day(6, 8);
        var d2 = d1.AddDays(1);
        var builder = new SnapshotBuilder()
            .Days(Enumerable.Range(-5, 9).Select(i => d1.AddDays(i)).ToArray())
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "A", maxConsecutiveDays: 6)
            .Agent(Agent(2), "B", maxConsecutiveDays: 6)
            .Agent(Agent(3), "C", maxConsecutiveDays: 6)
            .Agent(Agent(4), "X", maxConsecutiveDays: 6, isInGroup: false)
            .Work(Agent(1), d1, Shift(1), ShiftCategory.Early, At(d1, 7), At(d1, 15), 8m)
            .Work(Agent(1), d2, Shift(1), ShiftCategory.Early, At(d2, 7), At(d2, 15), 8m)
            .Work(Agent(3), d2, Shift(2), ShiftCategory.Early, At(d2, 7), At(d2, 15), 8m)
            .Availability(Agent(2), d1, new DayAvailability(true, false, false, IsOnCall: true));
        for (var i = -5; i <= -1; i++)
        {
            var day = d1.AddDays(i);
            builder.Work(Agent(2), day, Shift(3), ShiftCategory.Early, At(day, 7), At(day, 15), 8m);
        }

        return (builder.Build(), new AbsenceEvent(Agent(1), [d1, d2]));
    }
}
