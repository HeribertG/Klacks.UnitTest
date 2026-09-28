// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the per-planning-unit summary: duties and employees in the unit's scope (including sub-groups),
/// duties nobody in the unit can take today (F2 before proposals, globally unfillable duties counted
/// separately as F1), employees without an active contract in the period, the dominant blocking reason only
/// when more than half of the unit's employees share it, the capacity shortfall days of the unit (F7),
/// the focus subtree and a deterministic order by unit name.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingUnitSummaryBuilderTests
{
    private static readonly Guid East = Guid.NewGuid();
    private static readonly Guid EastChild = Guid.NewGuid();
    private static readonly Guid West = Guid.NewGuid();

    private readonly List<GroupingMembershipRecord> _memberships = [];
    private readonly HashSet<Guid> _clients = [];
    private readonly HashSet<Guid> _shifts = [];
    private FakeEligibilityOracle _oracle = null!;

    private static GroupingGroupTree Tree() => new(
    [
        new GroupingGroupRecord(East, "Ost", null, null, null, null),
        new GroupingGroupRecord(EastChild, "Ost Kind", East, null, null, null),
        new GroupingGroupRecord(West, "West", null, null, null, null),
    ]);

    [SetUp]
    public void SetUp()
    {
        _memberships.Clear();
        _clients.Clear();
        _shifts.Clear();
        _oracle = new FakeEligibilityOracle();
    }

    private Guid Shift(Guid group)
    {
        var id = Guid.NewGuid();
        _shifts.Add(id);
        _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), group, null, id));
        return id;
    }

    private Guid Client(Guid group)
    {
        var id = Guid.NewGuid();
        _clients.Add(id);
        _memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), group, id, null));
        return id;
    }

    private IReadOnlyList<GroupingUnitSummary> Build(IReadOnlyList<GroupingFinding>? findings = null, Guid? focus = null)
    {
        var tree = Tree();
        var state = GroupingMembershipState.From(_memberships, tree, _clients, _shifts);
        return GroupingUnitSummaryBuilder.Build(tree, state, _oracle, findings ?? [], focus);
    }

    [Test]
    public void UnitWhoseEmployeesAllLackAContract_ReportsEveryDutyAsUncoveredAndTheDominantReason()
    {
        Shift(East);
        Shift(East);
        for (var i = 0; i < 3; i++)
        {
            _oracle.WithoutContract(Client(East));
        }

        var unit = Build().ShouldHaveSingleItem();

        unit.UnitId.ShouldBe(East);
        unit.DutiesAnalysed.ShouldBe(2);
        unit.DutiesUncoveredInUnit.ShouldBe(2);
        unit.DutiesUnfillableGlobally.ShouldBe(0);
        unit.EmployeesInScope.ShouldBe(3);
        unit.EmployeesWithoutContract.ShouldBe(3);
        unit.DominantReason.ShouldBe(GroupingIneligibilityReason.NoActiveContract);
        unit.DominantReasonEmployees.ShouldBe(3);
        unit.HasGaps.ShouldBeTrue();
    }

    [Test]
    public void GloballyUnfillableDuty_IsCountedAsF1_NotAsF2()
    {
        var unfillable = Shift(East);
        var coverable = Shift(East);
        Client(East);

        var unit = Build([new GroupingFinding(GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: unfillable)])
            .ShouldHaveSingleItem();

        unit.DutiesUnfillableGlobally.ShouldBe(1);
        unit.DutiesUncoveredInUnit.ShouldBe(1);
        coverable.ShouldNotBe(unfillable);
    }

    [Test]
    public void CoveredUnit_HasNoGaps()
    {
        var shift = Shift(East);
        var client = Client(East);
        _oracle.Allow(client, shift);

        var unit = Build().ShouldHaveSingleItem();

        unit.DutiesUncoveredInUnit.ShouldBe(0);
        unit.DominantReason.ShouldBeNull();
        unit.HasGaps.ShouldBeFalse();
    }

    [Test]
    public void DominantReason_NeedsMoreThanHalfOfTheUnitsEmployees()
    {
        var shift = Shift(East);
        var fits = Client(East);
        var alsoFits = Client(East);
        _oracle.Allow(fits, shift).Allow(alsoFits, shift);
        _oracle.Deny(Client(East), shift, GroupingIneligibilityReason.Blacklisted);
        _oracle.Deny(Client(East), shift, GroupingIneligibilityReason.Blacklisted);

        Build().ShouldHaveSingleItem().DominantReason.ShouldBeNull();
    }

    [Test]
    public void DominantReason_IsReportedWhenMoreThanHalfShareIt()
    {
        var shift = Shift(East);
        _oracle.Allow(Client(East), shift);
        for (var i = 0; i < 3; i++)
        {
            _oracle.Deny(Client(East), shift, GroupingIneligibilityReason.Blacklisted);
        }

        var unit = Build().ShouldHaveSingleItem();

        unit.DominantReason.ShouldBe(GroupingIneligibilityReason.Blacklisted);
        unit.DominantReasonEmployees.ShouldBe(3);
        unit.EmployeesInScope.ShouldBe(4);
        unit.EmployeesWithoutContract.ShouldBe(0);
    }

    [Test]
    public void EmployeeWhoFitsOneOfTheUnitsDuties_IsNotBlocked()
    {
        var first = Shift(East);
        var second = Shift(East);
        var client = Client(East);
        _oracle.Allow(client, first).Deny(client, second, GroupingIneligibilityReason.Blacklisted);

        var unit = Build().ShouldHaveSingleItem();

        unit.DominantReason.ShouldBeNull();
        unit.DutiesUncoveredInUnit.ShouldBe(1);
    }

    [Test]
    public void ParentUnitScope_IncludesSubGroupDutiesAndEmployees()
    {
        Shift(East);
        Shift(EastChild);
        _oracle.WithoutContract(Client(EastChild));

        var units = Build();

        var east = units.Single(unit => unit.UnitId == East);
        east.DutiesAnalysed.ShouldBe(2);
        east.EmployeesInScope.ShouldBe(1);
        east.EmployeesWithoutContract.ShouldBe(1);
        units.Single(unit => unit.UnitId == EastChild).DutiesAnalysed.ShouldBe(1);
    }

    [Test]
    public void CapacityShortfalls_AreAttachedToTheirUnitInWeekdayOrder()
    {
        Shift(East);
        Shift(West);
        IReadOnlyList<GroupingFinding> findings =
        [
            new(GroupingFindingCode.CapacityShortfall, ReportOnly: true, GroupId: East, Weekday: DayOfWeek.Sunday, Demand: 24, Supply: 1),
            new(GroupingFindingCode.CapacityShortfall, ReportOnly: true, GroupId: East, Weekday: DayOfWeek.Monday, Demand: 88, Supply: 1),
        ];

        var east = Build(findings).Single(unit => unit.UnitId == East);

        east.CapacityGaps.Select(gap => gap.Weekday).ShouldBe([DayOfWeek.Monday, DayOfWeek.Sunday]);
        east.CapacityGaps[0].Demand.ShouldBe(88);
        east.CapacityGaps[0].Supply.ShouldBe(1);
        east.HasGaps.ShouldBeTrue();
        Build(findings).Single(unit => unit.UnitId == West).CapacityGaps.ShouldBeEmpty();
    }

    [Test]
    public void GroupThatOnlyBecomesAUnitThroughProposals_IsSummarisedWhenItHasACapacityShortfall()
    {
        Shift(East);
        IReadOnlyList<GroupingFinding> findings =
        [
            new(GroupingFindingCode.CapacityShortfall, ReportOnly: true, GroupId: West, Weekday: DayOfWeek.Monday, Demand: 2, Supply: 0),
        ];

        Build(findings).Select(unit => unit.UnitId).ShouldBe([East, West]);
    }

    [Test]
    public void Focus_LimitsTheSummaryToTheSubtree()
    {
        Shift(East);
        Shift(West);

        Build(focus: West).Select(unit => unit.UnitId).ShouldBe([West]);
    }

    [Test]
    public void Units_AreOrderedByName()
    {
        Shift(West);
        Shift(EastChild);
        Shift(East);

        Build().Select(unit => unit.UnitId).ShouldBe([East, EastChild, West]);
    }
}
