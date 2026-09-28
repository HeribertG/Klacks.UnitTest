// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins F1-F6 of the grouping plan: global unfillability with the most frequent reason, greedy cover of
/// uncovered shifts with its tie-breakers, placement of ungrouped shifts and clients, creation of a group
/// only when none exists, and the guarded removal of dead memberships (upcoming works, last group,
/// ancestors, pure people groups, adds before removals). F3 ranking of an ungrouped shift: a split shift
/// whose original has groups joins all of them (like ShiftCloner copies the original's group items);
/// else a located shift joins the nearest planning unit with an eligible client (nearest unit when none
/// has one); else the unit with the most eligible clients.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingPlanBuilderTests
{
    private static readonly TimeOnly Start = new(6, 0);
    private static readonly TimeOnly End = new(14, 0);

    private readonly List<GroupingGroupRecord> _groups = [];
    private readonly List<GroupingClientRecord> _clients = [];
    private readonly List<GroupingShiftRecord> _shifts = [];
    private readonly List<GroupingMembershipRecord> _memberships = [];
    private readonly HashSet<GroupingEntityPair> _futureWorks = [];
    private FakeEligibilityOracle _oracle = null!;

    [SetUp]
    public void SetUp()
    {
        _groups.Clear();
        _clients.Clear();
        _shifts.Clear();
        _memberships.Clear();
        _futureWorks.Clear();
        _oracle = new FakeEligibilityOracle();
    }

    private Guid Group(string name, Guid? parent = null, double? lat = null, double? lon = null)
    {
        var id = Guid.NewGuid();
        _groups.Add(new GroupingGroupRecord(id, name, parent, null, lat, lon));
        return id;
    }

    private Guid Client(string name, double? lat = null, double? lon = null, params Guid[] groups)
    {
        var id = Guid.NewGuid();
        _clients.Add(new GroupingClientRecord(id, name, lat, lon));
        foreach (var group in groups)
        {
            _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), group, id, null));
        }

        return id;
    }

    private Guid Shift(string name, params Guid[] groups)
    {
        var id = Guid.NewGuid();
        _shifts.Add(new GroupingShiftRecord(id, name, Start, End, 1));
        foreach (var group in groups)
        {
            _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), group, null, id));
        }

        return id;
    }

    private Guid DerivedShift(string name, Guid? originalId = null, double? lat = null, double? lon = null)
    {
        var id = Guid.NewGuid();
        _shifts.Add(new GroupingShiftRecord(id, name, Start, End, 1, originalId, null, lat, lon));
        return id;
    }

    private void OriginalIn(Guid originalId, params Guid[] groups)
    {
        foreach (var group in groups)
        {
            _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), group, null, originalId));
        }
    }

    private GroupingPlanResult Build(Guid? focus = null) => new GroupingPlanBuilder(new GroupingPlanInput(
        _clients, _shifts, new GroupingGroupTree(_groups), _memberships, _futureWorks, _oracle, focus)).Build();

    [Test]
    public void F1_ShiftNobodyCanTake_ReportsMostFrequentReason()
    {
        var unit = Group("Unit");
        var shift = Shift("Night", unit);
        var a = Client("A", groups: new[] { unit });
        var b = Client("B", groups: new[] { unit });
        var c = Client("C", groups: new[] { unit });
        _oracle.Deny(a, shift, GroupingIneligibilityReason.NotShiftWorker)
            .Deny(b, shift, GroupingIneligibilityReason.NotShiftWorker)
            .Deny(c, shift, GroupingIneligibilityReason.Blacklisted);

        var finding = Build().Findings.Single(f => f.Code == GroupingFindingCode.ShiftUnfillableGlobally);

        finding.ShiftId.ShouldBe(shift);
        finding.ReportOnly.ShouldBeTrue();
        finding.Reason.ShouldBe(GroupingIneligibilityReason.NotShiftWorker);
        finding.ReasonCounts!.First().Count.ShouldBe(2);
    }

    [Test]
    public void F2_UncoveredShifts_GreedyPicksTheClientCoveringMost()
    {
        var unit = Group("Unit");
        var other = Group("Other");
        var s1 = Shift("S1", unit);
        var s2 = Shift("S2", unit);
        Client("Inside", groups: new[] { unit });
        var wide = Client("Wide", groups: new[] { other });
        var narrow = Client("Narrow", groups: new[] { other });
        _oracle.Allow(wide, s1).Allow(wide, s2).Allow(narrow, s1);

        var result = Build();

        var adds = result.Proposals.Where(p => p.Kind == GroupingProposalKind.AddClient && p.GroupId == unit).ToList();
        adds.Count.ShouldBe(1);
        adds[0].ClientId.ShouldBe(wide);
        result.Findings.Count(f => f.Code == GroupingFindingCode.ShiftUncoveredInGroup && !f.ReportOnly).ShouldBe(2);
    }

    [Test]
    public void F2_TieBreak_FewerExistingGroupsWins()
    {
        var unit = Group("Unit");
        var g1 = Group("G1");
        var g2 = Group("G2");
        var shift = Shift("S", unit);
        var busy = Client("Aaa", groups: new[] { g1, g2 });
        var free = Client("Zzz", groups: new[] { g1 });
        _oracle.Allow(busy, shift).Allow(free, shift);

        Build().Proposals.Single(p => p.Kind == GroupingProposalKind.AddClient).ClientId.ShouldBe(free);
    }

    [Test]
    public void F2_TieBreak_ShorterDistanceWinsBeforeName()
    {
        var unit = Group("Unit", lat: 46.95, lon: 7.44);
        var g1 = Group("G1");
        var shift = Shift("S", unit);
        var far = Client("Aaa", 47.37, 8.54, g1);
        var near = Client("Zzz", 46.94, 7.45, g1);
        _oracle.Allow(far, shift).Allow(near, shift);

        Build().Proposals.Single(p => p.Kind == GroupingProposalKind.AddClient).ClientId.ShouldBe(near);
    }

    [Test]
    public void F3_UngroupedShift_GoesToTheUnitWithMostEligibleClients()
    {
        var small = Group("Small");
        var large = Group("Large");
        Shift("Anchor1", small);
        Shift("Anchor2", large);
        var loose = Shift("Loose");
        var a = Client("A", groups: new[] { large });
        var b = Client("B", groups: new[] { large });
        var c = Client("C", groups: new[] { small });
        _oracle.Allow(a, loose).Allow(b, loose).Allow(c, loose);

        var proposal = Build().Proposals.Single(p => p.Kind == GroupingProposalKind.AddShift);

        proposal.ShiftId.ShouldBe(loose);
        proposal.GroupId.ShouldBe(large);
    }

    [Test]
    public void F3_NoGroupAtAll_ProposesCreateGroupOnceAndPlacesEverything()
    {
        var s1 = Shift("S1");
        var s2 = Shift("S2");
        var a = Client("A");
        _oracle.Allow(a, s1).Allow(a, s2);

        var proposals = Build().Proposals;

        proposals.Count(p => p.Kind == GroupingProposalKind.CreateGroup).ShouldBe(1);
        proposals.Where(p => p.Kind == GroupingProposalKind.AddShift)
            .ShouldAllBe(p => p.GroupId == null && p.NewGroupKey == GroupingFeasibilityDefaults.NewGroupKey);
        proposals.ShouldContain(p => p.Kind == GroupingProposalKind.AddClient && p.ClientId == a && p.NewGroupKey != null);
    }

    [Test]
    public void F3_UnfillableUngroupedShift_IsNotPlaced()
    {
        var unit = Group("Unit");
        var anchor = Shift("Anchor", unit);
        var loose = Shift("Loose");
        var a = Client("A", groups: new[] { unit });
        _oracle.Allow(a, anchor);

        var result = Build();

        result.Proposals.ShouldNotContain(p => p.ShiftId == loose);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ShiftUnfillableGlobally && f.ShiftId == loose);
    }

    [Test]
    public void F3_SplitShiftWithGroupedOriginal_JoinsAllGroupsOfTheOriginal_NotTheTopRankedUnit()
    {
        var zurich = Group("Deutschschweiz Zürich");
        var west = Group("Westschweiz");
        var east = Group("Deutschschweiz Ost");
        Shift("AnchorZ", zurich);
        Shift("AnchorW", west);
        var original = Guid.NewGuid();
        OriginalIn(original, west, east);
        var split = DerivedShift("Split", originalId: original);
        var a = Client("A", groups: new[] { zurich });
        var b = Client("B", groups: new[] { zurich });
        var c = Client("C", groups: new[] { west });
        _oracle.Allow(a, split).Allow(b, split).Allow(c, split);

        var result = Build();

        result.Proposals.Where(p => p.Kind == GroupingProposalKind.AddShift && p.ShiftId == split)
            .Select(p => p.GroupId).ShouldBe(new Guid?[] { west, east }, ignoreOrder: true);
        result.Findings.Where(f => f.Code == GroupingFindingCode.ShiftWithoutGroup && f.ShiftId == split)
            .Select(f => f.GroupId).ShouldBe(new Guid?[] { west, east }, ignoreOrder: true);
    }

    [Test]
    public void F3_SplitShiftWhoseOriginalHasNoGroup_FallsBackToTheEligibleCountRanking()
    {
        var small = Group("Small");
        var large = Group("Large");
        Shift("Anchor1", small);
        Shift("Anchor2", large);
        var split = DerivedShift("Split", originalId: Guid.NewGuid());
        var a = Client("A", groups: new[] { large });
        var b = Client("B", groups: new[] { large });
        var c = Client("C", groups: new[] { small });
        _oracle.Allow(a, split).Allow(b, split).Allow(c, split);

        Build().Proposals.Single(p => p.Kind == GroupingProposalKind.AddShift).GroupId.ShouldBe(large);
    }

    [Test]
    public void F3_LocatedShift_JoinsTheNearestUnitWithAnEligibleClient()
    {
        var nearestWithoutEligible = Group("Nearest", lat: 47.00, lon: 8.00);
        var near = Group("Near", lat: 47.10, lon: 8.00);
        var far = Group("Far", lat: 46.20, lon: 6.10);
        Shift("A1", nearestWithoutEligible);
        Shift("A2", near);
        Shift("A3", far);
        var loose = DerivedShift("Loose", lat: 47.00, lon: 8.00);
        var a = Client("A", groups: new[] { near });
        var b = Client("B", groups: new[] { far });
        var c = Client("C", groups: new[] { far });
        Client("D", groups: new[] { nearestWithoutEligible });
        _oracle.Allow(a, loose).Allow(b, loose).Allow(c, loose);

        Build().Proposals.Single(p => p.Kind == GroupingProposalKind.AddShift).GroupId.ShouldBe(near);
    }

    [Test]
    public void F3_LocatedShiftWithoutEligibleClientAnywhere_JoinsTheNearestUnit()
    {
        var near = Group("Near", lat: 47.10, lon: 8.00);
        var far = Group("Far", lat: 46.20, lon: 6.10);
        var anchorNear = Shift("A1", near);
        Shift("A2", far);
        var loose = DerivedShift("Loose", lat: 47.00, lon: 8.00);
        var a = Client("A", groups: new[] { far });
        var b = Client("B", groups: new[] { far });
        var outsider = Client("Outsider");
        _oracle.Allow(outsider, loose).Allow(a, anchorNear);

        Build().Proposals.Single(p => p.Kind == GroupingProposalKind.AddShift).GroupId.ShouldBe(near);
    }

    [Test]
    public void F3_LocatedShiftButNoUnitHasCoordinates_UsesTheEligibleCountRanking()
    {
        var small = Group("Small");
        var large = Group("Large");
        Shift("Anchor1", small);
        Shift("Anchor2", large);
        var loose = DerivedShift("Loose", lat: 47.00, lon: 8.00);
        var a = Client("A", groups: new[] { large });
        var b = Client("B", groups: new[] { large });
        var c = Client("C", groups: new[] { small });
        _oracle.Allow(a, loose).Allow(b, loose).Allow(c, loose);

        Build().Proposals.Single(p => p.Kind == GroupingProposalKind.AddShift).GroupId.ShouldBe(large);
    }

    [Test]
    public void F4_UngroupedClient_JoinsTheUnitWithMostFits()
    {
        var one = Group("One");
        var two = Group("Two");
        var s1 = Shift("S1", one);
        var s2 = Shift("S2", two);
        var s3 = Shift("S3", two);
        var loose = Client("Loose");
        _oracle.Allow(loose, s1).Allow(loose, s2).Allow(loose, s3);

        var proposal = Build().Proposals.First(p => p.Kind == GroupingProposalKind.AddClient && p.ClientId == loose);

        proposal.GroupId.ShouldBe(two);
        proposal.Cause.ShouldBe(GroupingFindingCode.ClientWithoutGroup);
    }

    [Test]
    public void F5_ClientFitsNoShift_IsReportedWithReason()
    {
        var unit = Group("Unit");
        var shift = Shift("S", unit);
        var lonely = Client("Lonely", groups: new[] { unit });
        var worker = Client("Worker", groups: new[] { unit });
        _oracle.Allow(worker, shift).Deny(lonely, shift, GroupingIneligibilityReason.NoActiveContract);

        var finding = Build().Findings.Single(f => f.Code == GroupingFindingCode.ClientFitsNoShift);

        finding.ClientId.ShouldBe(lonely);
        finding.Reason.ShouldBe(GroupingIneligibilityReason.NoActiveContract);
        finding.ReportOnly.ShouldBeTrue();
    }

    [Test]
    public void F6_DeadMember_IsRemovedWhenNoUpcomingWorkAndAnotherGroupRemains()
    {
        var unit = Group("Unit");
        var home = Group("Home");
        var shift = Shift("S", unit);
        var worker = Client("Worker", groups: new[] { unit });
        var dead = Client("Dead", groups: new[] { unit, home });
        _oracle.Allow(worker, shift);

        var result = Build();

        result.Proposals.ShouldContain(p => p.Kind == GroupingProposalKind.RemoveClient && p.ClientId == dead && p.GroupId == unit);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ClientDeadMembership && f.ClientId == dead && !f.ReportOnly);
    }

    [Test]
    public void F6_DeadMemberWithUpcomingWork_IsOnlyReported()
    {
        var unit = Group("Unit");
        var home = Group("Home");
        var shift = Shift("S", unit);
        var worker = Client("Worker", groups: new[] { unit });
        var dead = Client("Dead", groups: new[] { unit, home });
        _oracle.Allow(worker, shift);
        _futureWorks.Add(new GroupingEntityPair(dead, shift));

        var result = Build();

        result.Proposals.ShouldNotContain(p => p.Kind == GroupingProposalKind.RemoveClient);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ClientDeadMembership && f.ClientId == dead && f.ReportOnly);
    }

    [Test]
    public void F6_DeadMemberInItsOnlyGroup_IsOnlyReported()
    {
        var unit = Group("Unit");
        var shift = Shift("S", unit);
        var worker = Client("Worker", groups: new[] { unit });
        var dead = Client("Dead", groups: new[] { unit });
        _oracle.Allow(worker, shift);

        var result = Build();

        result.Proposals.ShouldNotContain(p => p.Kind == GroupingProposalKind.RemoveClient);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ClientDeadMembership && f.ClientId == dead && f.ReportOnly);
    }

    [Test]
    public void F6_MemberUsefulInAnAncestorScope_IsKept()
    {
        var parent = Group("Parent");
        var child = Group("Child", parent);
        var home = Group("Home");
        var childShift = Shift("ChildShift", child);
        var parentShift = Shift("ParentShift", parent);
        var member = Client("Member", groups: new[] { child, home });
        var worker = Client("Worker", groups: new[] { child });
        _oracle.Allow(member, parentShift).Allow(worker, childShift);

        Build().Proposals.ShouldNotContain(p => p.Kind == GroupingProposalKind.RemoveClient && p.ClientId == member);
    }

    [Test]
    public void F6_SiblingMembershipKeepsTheAncestorScope_SoTheDeadOneIsRemoved()
    {
        var parent = Group("Parent");
        var first = Group("First", parent);
        var second = Group("Second", parent);
        var firstShift = Shift("FirstShift", first);
        var secondShift = Shift("SecondShift", second);
        var member = Client("Member", groups: new[] { first, second });
        var worker = Client("Worker", groups: new[] { first });
        _oracle.Allow(member, secondShift).Allow(worker, firstShift);

        Build().Proposals.ShouldContain(p => p.Kind == GroupingProposalKind.RemoveClient && p.ClientId == member && p.GroupId == first);
    }

    [Test]
    public void F6_PurePeopleGroupWithoutShifts_IsNeverThinned()
    {
        var department = Group("Department");
        var unit = Group("Unit");
        var shift = Shift("S", unit);
        var worker = Client("Worker", groups: new[] { unit, department });
        Client("Office", groups: new[] { department, unit });
        _oracle.Allow(worker, shift);

        Build().Proposals.ShouldNotContain(p => p.Kind == GroupingProposalKind.RemoveClient && p.GroupId == department);
    }

    [Test]
    public void Subtree_ClientOfChildCoversShiftOfParent()
    {
        var parent = Group("Parent");
        var child = Group("Child", parent);
        var shift = Shift("S", parent);
        var member = Client("Member", groups: new[] { child });
        _oracle.Allow(member, shift);

        Build().Findings.ShouldNotContain(f => f.Code == GroupingFindingCode.ShiftUncoveredInGroup);
    }

    [Test]
    public void AddsComeBeforeRemovals()
    {
        var unit = Group("Unit");
        var other = Group("Other");
        var shift = Shift("S", unit);
        var helper = Client("Helper", groups: new[] { other });
        Client("Dead", groups: new[] { unit, other });
        _oracle.Allow(helper, shift);

        var kinds = Build().Proposals.Select(p => p.Kind).ToList();

        kinds.IndexOf(GroupingProposalKind.AddClient).ShouldBeLessThan(kinds.IndexOf(GroupingProposalKind.RemoveClient));
    }

    [Test]
    public void Focus_RestrictsFindingsToTheSubtreeAndSkipsUngrouped()
    {
        var focus = Group("Focus");
        var elsewhere = Group("Elsewhere");
        Shift("InFocus", focus);
        Shift("Outside", elsewhere);
        Shift("Loose");

        var findings = Build(focus).Findings;

        findings.ShouldNotContain(f => f.Code == GroupingFindingCode.ShiftWithoutGroup);
        findings.Count(f => f.Code == GroupingFindingCode.ShiftUnfillableGlobally).ShouldBe(1);
    }

    [Test]
    public void MembershipOfAShiftWithoutRunDays_IsIgnoredEverywhere()
    {
        var unit = Group("Unit");
        var other = Group("Other");
        var withoutRunDays = Guid.NewGuid();
        _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), unit, null, withoutRunDays));
        Shift("Running", other);
        var client = Client("Member", groups: new[] { unit });

        var result = Build();

        result.Findings.ShouldNotContain(f => f.ShiftId == withoutRunDays);
        result.Proposals.ShouldNotContain(p => p.ShiftId == withoutRunDays);
        result.Findings.ShouldNotContain(f => f.Code == GroupingFindingCode.ShiftUncoveredInGroup && f.GroupId == unit);
        result.State.IsPlanningUnit(unit).ShouldBeFalse();
        result.Findings.Single(f => f.Code == GroupingFindingCode.ClientFitsNoShift && f.ClientId == client)
            .ReasonCounts!.Sum(count => count.Count).ShouldBe(1);
    }

    [Test]
    public void NoAnalysedShiftAtAll_ReportsNoClientFitsNoShift()
    {
        var unit = Group("Unit");
        _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), unit, null, Guid.NewGuid()));
        Client("Member", groups: new[] { unit });
        Client("Loose");

        var result = Build();

        result.Findings.ShouldBeEmpty();
        result.Proposals.ShouldBeEmpty();
    }

    [Test]
    public void F6_UpcomingWorkOnAShiftWithoutRunDays_StillBlocksRemoval()
    {
        var unit = Group("Unit");
        var home = Group("Home");
        var shift = Shift("S", unit);
        var withoutRunDays = Guid.NewGuid();
        _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), unit, null, withoutRunDays));
        var worker = Client("Worker", groups: new[] { unit });
        var dead = Client("Dead", groups: new[] { unit, home });
        _oracle.Allow(worker, shift);
        _futureWorks.Add(new GroupingEntityPair(dead, withoutRunDays));

        var result = Build();

        result.Proposals.ShouldNotContain(p => p.Kind == GroupingProposalKind.RemoveClient);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ClientDeadMembership && f.ClientId == dead && f.ReportOnly);
    }

    [Test]
    public void NoGroupAtAll_UngroupedEligibleClientJoinsTheNewGroup_WithoutReportFindings()
    {
        var shift = Shift("S1");
        var client = Client("A");
        _oracle.Allow(client, shift);

        var result = Build();

        result.Proposals.Select(p => p.Kind).ShouldBe(
            [GroupingProposalKind.CreateGroup, GroupingProposalKind.AddShift, GroupingProposalKind.AddClient]);
        result.Proposals.Where(p => p.Kind != GroupingProposalKind.CreateGroup)
            .ShouldAllBe(p => p.GroupId == null && p.NewGroupKey == GroupingFeasibilityDefaults.NewGroupKey);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ShiftWithoutGroup && f.ShiftId == shift && !f.ReportOnly);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ClientWithoutGroup && f.ClientId == client && !f.ReportOnly);
        result.Findings.ShouldNotContain(f => f.ReportOnly);
    }

    [Test]
    public void Focus_OnlyEligibleClientOutsideTheSubtree_IsAddedAndNothingIsReported()
    {
        var focus = Group("Focus");
        var elsewhere = Group("Elsewhere");
        var shift = Shift("S1", focus);
        Client("Inside", groups: new[] { focus });
        var outside = Client("Outside", groups: new[] { elsewhere });
        _oracle.Allow(outside, shift);

        var result = Build(focus);

        result.Proposals.ShouldContain(p => p.Kind == GroupingProposalKind.AddClient && p.GroupId == focus && p.ClientId == outside);
        result.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ShiftUncoveredInGroup && f.ShiftId == shift && !f.ReportOnly);
        result.Findings.ShouldNotContain(f => f.Code == GroupingFindingCode.ShiftUncoveredInGroup && f.ReportOnly);
    }
}
