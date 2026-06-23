// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Cross-group (tier 2) behaviour of the engine: a borrowable candidate from another group can cover a
/// slot via a direct cover that also emits a temporary <see cref="MembershipDelta"/>. In-group covers stay
/// strictly preferred (lower perturbation weight), cross-group candidates are still hard-gated, and the
/// emitted membership spans the full borrowing window. Cross-group swap chains (tier 3) are out of scope.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineCrossGroupTests
{
    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void In_group_candidate_is_preferred_over_an_available_cross_group_candidate()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "InGroup", isInGroup: true)
            .Agent(Agent(3), "CrossGroup", isInGroup: false)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(2));
        delta.Tier.ShouldBe(EscalationTier.InGroupFree);
        proposal.MembershipDeltas.ShouldBeEmpty();
    }

    [Test]
    public void Cross_group_candidate_covers_when_no_in_group_option_exists_and_emits_membership()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "InGroupButBlacklisted", isInGroup: true, blacklistedShiftIds: [s])
            .Agent(Agent(3), "CrossGroup", isInGroup: false)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(3));
        delta.Tier.ShouldBe(EscalationTier.CrossGroupFree);
        proposal.HighestTier.ShouldBe(EscalationTier.CrossGroupFree);

        var membership = proposal.MembershipDeltas.Single();
        membership.AgentId.ShouldBe(Agent(3));
        membership.GroupId.ShouldBe(Group(1));
        membership.ValidFrom.ShouldBe(d);
        membership.ValidUntil.ShouldBe(d);
    }

    [Test]
    public void Cross_group_candidate_is_hard_gated_when_ineligible()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(2), "InGroupBlacklisted", isInGroup: true, blacklistedShiftIds: [s])
            .Agent(Agent(3), "CrossGroupIneligible", isInGroup: false)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Ineligible(Agent(3), s, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.ShouldBeEmpty();
        proposal.MembershipDeltas.ShouldBeEmpty();
        proposal.Uncovered.Single().Reason.ShouldBe(RecoveryReasons.NoEligibleCandidate);
    }

    [Test]
    public void Multi_day_cross_group_cover_emits_one_membership_spanning_the_window()
    {
        var d0 = Day(6, 3);
        var d1 = Day(6, 4);
        var s0 = Shift(1);
        var s1 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d0, d1)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "A")
            .Agent(Agent(3), "CrossGroup", isInGroup: false)
            .Work(Agent(1), d0, s0, ShiftCategory.Early, At(d0, 8), At(d0, 16), 8m)
            .Work(Agent(1), d1, s1, ShiftCategory.Late, At(d1, 14), At(d1, 22), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d0, d1]), Ruleset.Default);

        proposal.Deltas.Count.ShouldBe(2);
        proposal.Deltas.ShouldAllBe(x => x.Tier == EscalationTier.CrossGroupFree);
        var membership = proposal.MembershipDeltas.Single();
        membership.AgentId.ShouldBe(Agent(3));
        membership.ValidFrom.ShouldBe(d0);
        membership.ValidUntil.ShouldBe(d1);
    }
}
