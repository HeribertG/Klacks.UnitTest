// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Golden-master tests for the swap chains: a relief that does not overlap the demand (early against late), a relief
/// on another day (the block is extended and a later work handed on), chains of three moves, the borrowed agent as
/// the end of a chain, the first relief day, the probe budget and the rest-time boundary of a direct cover.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineChainTests
{
    private const decimal RestHours = 11m;
    private const int FirstBlocker = 2;
    private const int LastBlocker = 5;
    private const int BlockerWorks = 6;

    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void Early_holder_takes_the_late_demand_and_hands_its_early_on()
    {
        // Arrange: A's late is open. B is the only one qualified for it but holds the early of the same day, which
        // ends when the late starts: no overlap, but no rest time either. C can take B's early.
        var d = Day(6, 3);
        var late = Shift(1);
        var early = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", minPauseHours: RestHours)
            .Agent(Agent(3), "C", minPauseHours: RestHours)
            .Work(Agent(1), d, late, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Work(Agent(2), d, early, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Ineligible(Agent(3), late, d)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Select(x => (x.ShiftId, x.Date, x.FromAgentId, x.ToAgentId, x.Tier)).ShouldBe(
        [
            (early, d, Agent(2), Agent(3), EscalationTier.InGroupSwap),
            (late, d, Agent(1), Agent(2), EscalationTier.InGroupSwap),
        ]);
        proposal.Objective.ShouldBe(new RecoveryObjective(0, 0, 2 * RulesetDefaults.WeightInGroupSwap, 2));
    }

    [Test]
    public void Free_agent_extends_its_block_and_hands_a_later_work_on()
    {
        // Arrange: B is free on the demand day but works the three days after it; with at most three days in a row
        // the demand would make four. C is not qualified for the demand but can take one of B's own works.
        var d = Day(6, 1);
        var demandShift = Shift(1);
        var own = Shift(2);
        var builder = new SnapshotBuilder()
            .Days(d, d.AddDays(1), d.AddDays(2), d.AddDays(3))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxConsecutiveDays: 3)
            .Agent(Agent(3), "C", maxConsecutiveDays: 3)
            .Work(Agent(1), d, demandShift, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Ineligible(Agent(3), demandShift, d);
        for (var offset = 1; offset <= 3; offset++)
        {
            var day = d.AddDays(offset);
            builder.Work(Agent(2), day, own, ShiftCategory.Early, At(day, 6), At(day, 14), 8m);
        }

        // Act
        var proposal = _engine.Repair(builder.Build(), new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert: B starts one day earlier and gives its last day away, so its block stays in one piece.
        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Select(x => (x.ShiftId, x.Date, x.FromAgentId, x.ToAgentId)).ShouldBe(
        [
            (own, d.AddDays(3), Agent(2), Agent(3)),
            (demandShift, d, Agent(1), Agent(2)),
        ]);
    }

    [Test]
    public void Three_moves_cover_what_two_cannot()
    {
        // Arrange
        var (snapshot, absence, shifts) = BuildThreeMoveCase();

        // Act
        var proposal = _engine.Repair(snapshot, absence, Ruleset.Default);

        // Assert
        var d = absence.Dates[0];
        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Select(x => (x.ShiftId, x.FromAgentId, x.ToAgentId, x.Tier, x.OptionId)).ShouldBe(
        [
            (shifts.Other, Agent(3), Agent(4), EscalationTier.InGroupSwap, 0),
            (shifts.Early, Agent(2), Agent(3), EscalationTier.InGroupSwap, 0),
            (shifts.Late, Agent(1), Agent(2), EscalationTier.InGroupSwap, 0),
        ]);
        proposal.Deltas.ShouldAllBe(x => x.Date == d);
        proposal.Objective.Perturbation.ShouldBe(3 * RulesetDefaults.WeightInGroupSwap);
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupSwap);
    }

    [Test]
    public void Chain_depth_two_leaves_the_three_move_case_open()
    {
        // Arrange
        var (snapshot, absence, _) = BuildThreeMoveCase();

        // Act
        var proposal = _engine.Repair(snapshot, absence, Ruleset.Default with { MaxSwapChainDepth = 2 });

        // Assert
        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Single().Reason.ShouldBe(RecoveryReasons.NoEligibleCandidate);
    }

    [Test]
    public void A_spent_probe_budget_leaves_the_three_move_case_open()
    {
        // Arrange
        var (snapshot, absence, _) = BuildThreeMoveCase();

        // Act
        var proposal = _engine.Repair(snapshot, absence, Ruleset.Default with { DeepChainProbeBudget = 0 });

        // Assert
        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Count.ShouldBe(1);
    }

    [Test]
    public void Three_move_repair_is_deterministic()
    {
        // Arrange
        var (snapshot, absence, _) = BuildThreeMoveCase();

        // Act
        var first = Format(_engine.Repair(snapshot, absence, Ruleset.Default));
        var second = Format(_engine.Repair(snapshot, absence, Ruleset.Default));

        // Assert
        second.ShouldBe(first);
    }

    [Test]
    public void Two_moves_win_over_three()
    {
        // Arrange: the three-move case plus E, who is free and can take B's early directly.
        var d = Day(6, 3);
        var (late, early, other) = (Shift(1), Shift(2), Shift(3));
        var snapshot = ThreeMoveBuilder(d, late, early, other)
            .Agent(Agent(5), "E", minPauseHours: RestHours)
            .Ineligible(Agent(5), late, d)
            .Ineligible(Agent(5), other, d)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.Select(x => (x.ShiftId, x.FromAgentId, x.ToAgentId)).ShouldBe(
        [
            (early, Agent(2), Agent(5)),
            (late, Agent(1), Agent(2)),
        ]);
    }

    [Test]
    public void A_direct_cover_from_a_sister_group_wins_over_three_moves_in_the_group()
    {
        // Arrange: the three-move case plus X of a sister group, who can take the open late directly. Fewer moves
        // win, also across the group border (owner decision 2026-11-11).
        var d = Day(6, 3);
        var (late, early, other) = (Shift(1), Shift(2), Shift(3));
        var snapshot = ThreeMoveBuilder(d, late, early, other)
            .ReceivingGroup(Group(1))
            .Agent(Agent(9), "X", minPauseHours: RestHours, isInGroup: false)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.Select(x => (x.ShiftId, x.FromAgentId, x.ToAgentId, x.Tier)).ShouldBe(
        [
            (late, Agent(1), Agent(9), EscalationTier.CrossGroupFree),
        ]);
        proposal.MembershipDeltas.ShouldBe([new MembershipDelta(Agent(9), Group(1), d, d)]);
    }

    [Test]
    public void Borrowed_agent_ends_a_chain_with_one_temporary_membership()
    {
        // Arrange: nobody in the group can take B's early; X of a sister group can, but is not qualified for the late.
        var d = Day(6, 3);
        var late = Shift(1);
        var early = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", minPauseHours: RestHours)
            .Agent(Agent(4), "X", isInGroup: false)
            .Work(Agent(1), d, late, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Work(Agent(2), d, early, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Ineligible(Agent(4), late, d)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.Select(x => (x.ShiftId, x.FromAgentId, x.ToAgentId, x.Tier)).ShouldBe(
        [
            (early, Agent(2), Agent(4), EscalationTier.CrossGroupSwap),
            (late, Agent(1), Agent(2), EscalationTier.InGroupSwap),
        ]);
        proposal.MembershipDeltas.ShouldBe([new MembershipDelta(Agent(4), Group(1), d, d)]);
        proposal.HighestTier.ShouldBe(EscalationTier.CrossGroupSwap);
        proposal.Objective.Perturbation.ShouldBe(
            RulesetDefaults.WeightCrossGroupSwap + RulesetDefaults.WeightInGroupSwap);
    }

    [Test]
    public void Own_work_of_a_borrowed_agent_is_never_handed_on()
    {
        // Arrange: X of a sister group is the only one qualified for the demand, but holds an overlapping work of
        // its own. C could take that work, yet the works of a borrowed agent stay with it.
        var d = Day(6, 3);
        var demandShift = Shift(1);
        var foreign = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(3), "C")
            .Agent(Agent(4), "X", isInGroup: false)
            .Work(Agent(1), d, demandShift, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(4), d, foreign, ShiftCategory.Early, At(d, 7), At(d, 15), 8m)
            .Ineligible(Agent(3), demandShift, d)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Count.ShouldBe(1);
    }

    [Test]
    public void A_work_beyond_the_relief_reach_is_not_handed_on()
    {
        // Arrange: B is free on the demand day, yet the period cap leaves no room for it. The only work B could
        // give up lies one day beyond the reach.
        var d = Day(6, 1);
        var far = d.AddDays(RulesetDefaults.ReliefReachDays + 1);
        var demandShift = Shift(1);
        var own = Shift(2);
        var builder = new SnapshotBuilder()
            .Days(d, far)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maximumHours: 8m, currentPeriodHours: 8m)
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, demandShift, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(2), far, own, ShiftCategory.Early, At(far, 6), At(far, 14), 8m)
            .Ineligible(Agent(3), demandShift, d);

        // Act
        var beyond = _engine.Repair(builder.Build(), new AbsenceEvent(Agent(1), [d]), Ruleset.Default);
        var within = _engine.Repair(
            builder.Build(),
            new AbsenceEvent(Agent(1), [d]),
            Ruleset.Default with { ReliefReachDays = RulesetDefaults.ReliefReachDays + 1 });

        // Assert
        beyond.Deltas.ShouldBeEmpty();
        within.Deltas.Select(x => (x.Date, x.FromAgentId, x.ToAgentId)).ShouldBe(
        [
            (far, Agent(2), Agent(3)),
            (d, Agent(1), Agent(2)),
        ]);
    }

    [Test]
    public void A_work_on_a_day_sealed_for_its_holder_is_not_handed_on()
    {
        // Arrange: the block case, but the day of the work B would give away (its last day) is sealed for B, and
        // the two other days of the block are locked.
        var d = Day(6, 1);
        var demandShift = Shift(1);
        var own = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d, d.AddDays(1), d.AddDays(2), d.AddDays(3))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxConsecutiveDays: 3)
            .Agent(Agent(3), "C", maxConsecutiveDays: 3)
            .Work(Agent(1), d, demandShift, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(2), d.AddDays(1), own, ShiftCategory.Early, At(d.AddDays(1), 6), At(d.AddDays(1), 14), 8m, locked: true)
            .Work(Agent(2), d.AddDays(2), own, ShiftCategory.Early, At(d.AddDays(2), 6), At(d.AddDays(2), 14), 8m, locked: true)
            .Work(Agent(2), d.AddDays(3), own, ShiftCategory.Early, At(d.AddDays(3), 6), At(d.AddDays(3), 14), 8m)
            .SealedDay(Agent(2), d.AddDays(3))
            .Ineligible(Agent(3), demandShift, d)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Count.ShouldBe(1);
    }

    [Test]
    public void The_probe_budget_is_shared_so_a_late_agent_in_the_order_still_gets_its_turn()
    {
        // Arrange: the three-move case (B, C, D), preceded in the pool order by four agents who are free on the demand
        // day but at their period cap. Each could give one of six later works away, yet nobody may take those, so
        // each of them burns many probes without ever finding a chain. One budget for all would be spent before B.
        var d = Day(6, 1);
        var (late, early, other) = (Shift(1), Shift(2), Shift(3));
        var builder = new SnapshotBuilder().Agent(Agent(1), "A");
        for (var offset = 0; offset <= BlockerWorks; offset++)
        {
            builder.Days(d.AddDays(offset));
        }
        builder.Work(Agent(1), d, late, ShiftCategory.Late, At(d, 14), At(d, 22), 8m);
        for (var n = FirstBlocker; n <= LastBlocker; n++)
        {
            builder.Agent(Agent(n), "blocker" + n, maximumHours: 8m, currentPeriodHours: 8m);
            for (var offset = 1; offset <= BlockerWorks; offset++)
            {
                var day = d.AddDays(offset);
                builder.Work(Agent(n), day, Shift(10 + n), ShiftCategory.Early, At(day, 6), At(day, 14), 8m);
            }
        }
        builder
            .Agent(Agent(6), "B", minPauseHours: RestHours)
            .Agent(Agent(7), "C", minPauseHours: RestHours)
            .Agent(Agent(8), "D", minPauseHours: RestHours)
            .Work(Agent(6), d, early, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(7), d, other, ShiftCategory.Other, At(d, 8), At(d, 12), 4m)
            .Ineligible(Agent(7), late, d)
            .Ineligible(Agent(8), late, d);
        for (var n = FirstBlocker; n <= 8; n++)
        {
            for (var blocker = FirstBlocker; blocker <= LastBlocker; blocker++)
            {
                for (var offset = 1; offset <= BlockerWorks; offset++)
                {
                    builder.Ineligible(Agent(n), Shift(10 + blocker), d.AddDays(offset));
                }
            }
            if (n != 7)
            {
                builder.Ineligible(Agent(n), early, d);
            }
            if (n != 8)
            {
                builder.Ineligible(Agent(n), other, d);
            }
        }
        var snapshot = builder.Build();
        var absence = new AbsenceEvent(Agent(1), [d]);
        const int InGroupAgents = 7;
        const int ProbesPerAgent = 20;

        // Act
        var proposal = _engine.Repair(
            snapshot, absence, Ruleset.Default with { DeepChainProbeBudget = ProbesPerAgent * InGroupAgents });

        // Assert
        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Select(x => (x.FromAgentId, x.ToAgentId)).ShouldBe(
        [
            (Agent(7), Agent(8)),
            (Agent(6), Agent(7)),
            (Agent(1), Agent(6)),
        ]);
    }

    [Test]
    public void No_work_before_the_absence_is_handed_on()
    {
        // Arrange: B worked the three days before the demand day; only giving one of them away would let B cover.
        var (snapshot, absence) = BuildPastBlockCase(firstMovableDay: null);

        // Act
        var proposal = _engine.Repair(snapshot, absence, Ruleset.Default);

        // Assert
        proposal.Deltas.ShouldBeEmpty();
        proposal.Uncovered.Count.ShouldBe(1);
    }

    [Test]
    public void First_movable_day_opens_earlier_days_for_a_relief()
    {
        // Arrange: the same case, but the days before the absence still lie in the future.
        var d = Day(6, 4);
        var (snapshot, absence) = BuildPastBlockCase(firstMovableDay: d.AddDays(-3));

        // Act
        var proposal = _engine.Repair(snapshot, absence, Ruleset.Default);

        // Assert
        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Count.ShouldBe(2);
        proposal.Deltas[0].FromAgentId.ShouldBe(Agent(2));
        proposal.Deltas[0].ToAgentId.ShouldBe(Agent(3));
        proposal.Deltas[0].Date.ShouldBeLessThan(d);
        proposal.Deltas[1].ToAgentId.ShouldBe(Agent(2));
    }

    [Test]
    public void A_cover_the_repair_placed_is_not_handed_on_again()
    {
        // Arrange: B covers day one. On day two only B is qualified, and B may work one day in a row only. Handing
        // B's fresh cover of day one on to C would free B, but would move that work twice.
        var d1 = Day(6, 3);
        var d2 = d1.AddDays(1);
        var shift = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d1, d2)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxConsecutiveDays: 1)
            .Agent(Agent(3), "C", maxConsecutiveDays: 1)
            .Work(Agent(1), d1, shift, ShiftCategory.Early, At(d1, 6), At(d1, 14), 8m)
            .Work(Agent(1), d2, shift, ShiftCategory.Early, At(d2, 6), At(d2, 14), 8m)
            .Ineligible(Agent(3), shift, d2)
            .Build();

        // Act
        var greedy = _engine.Repair(
            snapshot, new AbsenceEvent(Agent(1), [d1, d2]), Ruleset.Default with { LookaheadBeamWidth = 1 });

        // Assert
        greedy.Deltas.Select(x => (x.Date, x.FromAgentId, x.ToAgentId)).ShouldBe([(d1, Agent(1), Agent(2))]);
        greedy.Uncovered.Single().Date.ShouldBe(d2);
    }

    [Test]
    public void Excluding_an_assignment_makes_the_repair_take_the_next_candidate()
    {
        // Arrange: B and C can both cover; the stable order picks B.
        var d = Day(6, 3);
        var shift = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B")
            .Agent(Agent(3), "C")
            .Work(Agent(1), d, shift, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Build();
        var absence = new AbsenceEvent(Agent(1), [d]);

        // Act
        var first = _engine.Repair(snapshot, absence, Ruleset.Default);
        var withoutAssignment = _engine.Repair(
            snapshot.Excluding([], [new IneligibleKey(Agent(2), shift, d)]), absence, Ruleset.Default);
        var withoutAgents = _engine.Repair(snapshot.Excluding([Agent(2), Agent(3)], []), absence, Ruleset.Default);

        // Assert
        first.Deltas.Single().ToAgentId.ShouldBe(Agent(2));
        withoutAssignment.Deltas.Single().ToAgentId.ShouldBe(Agent(3));
        withoutAgents.Deltas.ShouldBeEmpty();
        snapshot.Excluding([Agent(2)], []).FindAgent(Agent(2)).ShouldNotBeNull();
    }

    [Test]
    public void First_movable_day_set_afterwards_equals_the_one_built_in()
    {
        // Arrange
        var d = Day(6, 4);
        var (plain, absence) = BuildPastBlockCase(firstMovableDay: null);
        var (built, _) = BuildPastBlockCase(firstMovableDay: d.AddDays(-3));

        // Act
        var setAfterwards = _engine.Repair(plain.WithFirstMovableDay(d.AddDays(-3)), absence, Ruleset.Default);
        var builtIn = _engine.Repair(built, absence, Ruleset.Default);

        // Assert
        Format(setAfterwards).ShouldBe(Format(builtIn));
        setAfterwards.Deltas.Count.ShouldBe(2);
    }

    [TestCase(9, 0, true)]
    [TestCase(8, 59, false)]
    public void Late_before_early_is_a_cover_once_the_rest_time_is_kept(int startHour, int startMinute, bool covered)
    {
        // Arrange: B worked the late until 22:00 the evening before; the rest time is eleven hours.
        var d = Day(6, 3);
        var early = Shift(1);
        var late = Shift(2);
        var eve = d.AddDays(-1);
        var snapshot = new SnapshotBuilder()
            .Days(eve, d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", minPauseHours: RestHours)
            .Work(Agent(1), d, early, ShiftCategory.Early, At(d, startHour, startMinute), At(d, 17), 8m)
            .Work(Agent(2), eve, late, ShiftCategory.Late, At(eve, 14), At(eve, 22), 8m)
            .Build();

        // Act
        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        // Assert
        proposal.Deltas.Count.ShouldBe(covered ? 1 : 0);
        proposal.Uncovered.Count.ShouldBe(covered ? 0 : 1);
    }

    private static (RecoverySnapshot Snapshot, AbsenceEvent Absence, (Guid Late, Guid Early, Guid Other) Shifts)
        BuildThreeMoveCase()
    {
        var d = Day(6, 3);
        var shifts = (Late: Shift(1), Early: Shift(2), Other: Shift(3));
        var snapshot = ThreeMoveBuilder(d, shifts.Late, shifts.Early, shifts.Other).Build();
        return (snapshot, new AbsenceEvent(Agent(1), [d]), shifts);
    }

    /// <summary>
    /// A's late is open. Only B is qualified for it, but B holds the early and has no rest time. Only C is qualified
    /// for B's early, but C holds an overlapping other shift. Only D can take that one.
    /// </summary>
    private static SnapshotBuilder ThreeMoveBuilder(DateOnly d, Guid late, Guid early, Guid other)
        => new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", minPauseHours: RestHours)
            .Agent(Agent(3), "C", minPauseHours: RestHours)
            .Agent(Agent(4), "D", minPauseHours: RestHours)
            .Work(Agent(1), d, late, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Work(Agent(2), d, early, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Work(Agent(3), d, other, ShiftCategory.Other, At(d, 8), At(d, 12), 4m)
            .Ineligible(Agent(3), late, d)
            .Ineligible(Agent(4), late, d)
            .Ineligible(Agent(4), early, d);

    private static (RecoverySnapshot Snapshot, AbsenceEvent Absence) BuildPastBlockCase(DateOnly? firstMovableDay)
    {
        var d = Day(6, 4);
        var demandShift = Shift(1);
        var own = Shift(2);
        var builder = new SnapshotBuilder()
            .Days(d.AddDays(-3), d.AddDays(-2), d.AddDays(-1), d)
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "B", maxConsecutiveDays: 3)
            .Agent(Agent(3), "C", maxConsecutiveDays: 3)
            .Work(Agent(1), d, demandShift, ShiftCategory.Early, At(d, 6), At(d, 14), 8m)
            .Ineligible(Agent(3), demandShift, d);
        for (var offset = 1; offset <= 3; offset++)
        {
            var day = d.AddDays(-offset);
            builder.Work(Agent(2), day, own, ShiftCategory.Early, At(day, 6), At(day, 14), 8m);
        }
        if (firstMovableDay is { } first)
        {
            builder.FirstMovableDay(first);
        }

        return (builder.Build(), new AbsenceEvent(Agent(1), [d]));
    }
}
