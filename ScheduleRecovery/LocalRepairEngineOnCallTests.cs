// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// On-call agents are the preferred replacements: a direct cover of an agent on call that day gets its own
/// tier, ordered in-group on-call &lt; in-group free &lt; cross-group on-call &lt; in-group swap &lt;
/// cross-group free. The tier values are append-only, so the highest tier is judged by severity, not by
/// the enum's integer.
/// </summary>
[TestFixture]
public sealed class LocalRepairEngineOnCallTests
{
    private static readonly DayAvailability OnCall = new(true, false, false, IsOnCall: true);

    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void In_group_on_call_agent_wins_over_a_free_in_group_agent_with_lower_guid()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "Free", preferredShiftIds: [s], targetHoursDeficit: 40m)
            .Agent(Agent(3), "OnCall")
            .Availability(Agent(3), d, OnCall)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(3));
        delta.Tier.ShouldBe(EscalationTier.InGroupOnCall);
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupOnCall);
        proposal.Objective.Perturbation.ShouldBe(RulesetDefaults.WeightInGroupOnCall);
    }

    [Test]
    public void Cross_group_on_call_agent_ranks_after_a_free_in_group_agent()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "CrossOnCall", isInGroup: false)
            .Agent(Agent(3), "InGroupFree")
            .Availability(Agent(2), d, OnCall)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(3));
        delta.Tier.ShouldBe(EscalationTier.InGroupFree);
    }

    [Test]
    public void Cross_group_on_call_agent_wins_over_a_cross_group_free_agent()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "CrossFree", isInGroup: false)
            .Agent(Agent(3), "CrossOnCall", isInGroup: false)
            .Availability(Agent(3), d, OnCall)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(3));
        delta.Tier.ShouldBe(EscalationTier.CrossGroupOnCall);
        proposal.HighestTier.ShouldBe(EscalationTier.CrossGroupOnCall);
        proposal.MembershipDeltas.Single().AgentId.ShouldBe(Agent(3));
    }

    [Test]
    public void Cross_group_on_call_agent_wins_over_an_in_group_swap()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "Blocked")
            .Agent(Agent(3), "Recipient")
            .Agent(Agent(4), "CrossOnCall", isInGroup: false)
            .Availability(Agent(4), d, OnCall)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, s2, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Ineligible(Agent(3), s, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var delta = proposal.Deltas.Single();
        delta.ToAgentId.ShouldBe(Agent(4));
        delta.Tier.ShouldBe(EscalationTier.CrossGroupOnCall);
    }

    [Test]
    public void In_group_swap_still_wins_over_a_cross_group_free_agent()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "Blocked")
            .Agent(Agent(3), "Recipient")
            .Agent(Agent(4), "CrossFree", isInGroup: false)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Work(Agent(2), d, s2, ShiftCategory.Late, At(d, 14), At(d, 22), 8m)
            .Ineligible(Agent(3), s, d)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Count.ShouldBe(2);
        proposal.Deltas.ShouldAllBe(x => x.Tier == EscalationTier.InGroupSwap);
    }

    [Test]
    public void On_call_agent_still_respects_the_contract_gates()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "Free")
            .Agent(Agent(3), "OnCallButBlacklisted", blacklistedShiftIds: [s])
            .Availability(Agent(3), d, OnCall)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().ToAgentId.ShouldBe(Agent(2));
    }

    [Test]
    public void Two_on_call_agents_resolve_deterministically_by_guid()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "Absent")
            .Agent(Agent(4), "OnCallHigh")
            .Agent(Agent(3), "OnCallLow")
            .Agent(Agent(2), "Free")
            .Availability(Agent(4), d, OnCall)
            .Availability(Agent(3), d, OnCall)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 8), At(d, 16), 8m)
            .Build();

        var first = Format(_engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default));
        var second = Format(_engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default));

        first.ShouldBe(second);
        _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default)
            .Deltas.Single().ToAgentId.ShouldBe(Agent(3));
    }

    [Test]
    public void Highest_tier_is_uncovered_when_an_on_call_cover_and_an_open_slot_coexist()
    {
        var d = Day(6, 3);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d)
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "OnCall")
            .Availability(Agent(2), d, OnCall)
            .Work(Agent(1), d, s, ShiftCategory.Early, At(d, 6), At(d, 10), 4m)
            .Work(Agent(1), d, s2, ShiftCategory.Early, At(d, 8), At(d, 12), 4m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        proposal.Deltas.Single().Tier.ShouldBe(EscalationTier.InGroupOnCall);
        proposal.Uncovered.ShouldNotBeEmpty();
        proposal.HighestTier.ShouldBe(EscalationTier.Uncovered);
    }

    [TestCase(EscalationTier.InGroupSwap, EscalationTier.CrossGroupOnCall, EscalationTier.InGroupSwap)]
    [TestCase(EscalationTier.InGroupFree, EscalationTier.InGroupOnCall, EscalationTier.InGroupFree)]
    [TestCase(EscalationTier.CrossGroupOnCall, EscalationTier.InGroupFree, EscalationTier.CrossGroupOnCall)]
    public void Highest_picks_by_severity_from_mixed_tiers(EscalationTier a, EscalationTier b, EscalationTier expected)
    {
        EscalationTierSeverity.Highest([a, b], anyUncovered: false).ShouldBe(expected);
        EscalationTierSeverity.Highest([b, a], anyUncovered: false).ShouldBe(expected);
    }

    [Test]
    public void Highest_is_uncovered_when_a_slot_stayed_open_and_in_group_free_when_empty()
    {
        EscalationTierSeverity.Highest([EscalationTier.InGroupOnCall], anyUncovered: true).ShouldBe(EscalationTier.Uncovered);
        EscalationTierSeverity.Highest([], anyUncovered: false).ShouldBe(EscalationTier.InGroupFree);
    }

    [Test]
    public void Engine_highest_tier_of_a_swap_and_a_cross_group_on_call_cover_is_the_swap()
    {
        var d1 = Day(6, 3);
        var d2 = Day(6, 4);
        var s = Shift(1);
        var s2 = Shift(2);
        var snapshot = new SnapshotBuilder()
            .Days(d1, d2)
            .ReceivingGroup(Group(1))
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "Blocked")
            .Agent(Agent(3), "Recipient")
            .Agent(Agent(4), "CrossOnCall", isInGroup: false)
            .Unavailable(Agent(4), d1)
            .Availability(Agent(4), d2, OnCall)
            .Unavailable(Agent(2), d2)
            .Unavailable(Agent(3), d2)
            .Work(Agent(1), d1, s, ShiftCategory.Early, At(d1, 8), At(d1, 16), 8m)
            .Work(Agent(1), d2, s, ShiftCategory.Early, At(d2, 8), At(d2, 16), 8m)
            .Work(Agent(2), d1, s2, ShiftCategory.Late, At(d1, 14), At(d1, 22), 8m)
            .Ineligible(Agent(3), s, d1)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d1, d2]), Ruleset.Default);

        proposal.Uncovered.ShouldBeEmpty();
        proposal.Deltas.Select(x => x.Tier).ShouldBe(
            [EscalationTier.InGroupSwap, EscalationTier.InGroupSwap, EscalationTier.CrossGroupOnCall], ignoreOrder: true);
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupSwap);
    }

    [Test]
    public void Engine_highest_tier_of_an_on_call_and_a_free_cover_is_in_group_free()
    {
        var d1 = Day(6, 3);
        var d2 = Day(6, 4);
        var s = Shift(1);
        var snapshot = new SnapshotBuilder()
            .Days(d1, d2)
            .Agent(Agent(1), "Absent")
            .Agent(Agent(2), "OnCallMonday")
            .Agent(Agent(3), "Free")
            .Availability(Agent(2), d1, OnCall)
            .Unavailable(Agent(2), d2)
            .Work(Agent(1), d1, s, ShiftCategory.Early, At(d1, 8), At(d1, 16), 8m)
            .Work(Agent(1), d2, s, ShiftCategory.Early, At(d2, 8), At(d2, 16), 8m)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d1, d2]), Ruleset.Default);

        proposal.Deltas.Select(x => x.Tier).ShouldBe([EscalationTier.InGroupOnCall, EscalationTier.InGroupFree]);
        proposal.HighestTier.ShouldBe(EscalationTier.InGroupFree);
    }

    [Test]
    public void Severity_orders_the_appended_tiers_by_escalation_not_by_value()
    {
        EscalationTier[] ordered =
        [
            EscalationTier.InGroupOnCall,
            EscalationTier.InGroupFree,
            EscalationTier.CrossGroupOnCall,
            EscalationTier.InGroupSwap,
            EscalationTier.CrossGroupFree,
            EscalationTier.CrossGroupSwap,
            EscalationTier.Uncovered,
        ];

        ordered.Select(EscalationTierSeverity.Rank).ShouldBe(ordered.Select(EscalationTierSeverity.Rank).Order());
        ordered.Select(EscalationTierSeverity.Rank).Distinct().Count().ShouldBe(ordered.Length);
        Enum.GetValues<EscalationTier>().Length.ShouldBe(ordered.Length);
    }

    [Test]
    public void Tier_values_reaching_the_api_stay_stable()
    {
        ((int)EscalationTier.InGroupFree).ShouldBe(0);
        ((int)EscalationTier.InGroupSwap).ShouldBe(1);
        ((int)EscalationTier.CrossGroupFree).ShouldBe(2);
        ((int)EscalationTier.CrossGroupSwap).ShouldBe(3);
        ((int)EscalationTier.Uncovered).ShouldBe(4);
        ((int)EscalationTier.InGroupOnCall).ShouldBe(5);
        ((int)EscalationTier.CrossGroupOnCall).ShouldBe(6);
    }
}
