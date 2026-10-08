// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// Order tree as the cut dialog leaves it: the sealed order A, its former plannable copy R (turned into the top-level
/// cut piece by the first cut), two pieces C1 and C2 cut from R, a piece G cut from C1, and an unrelated order X.
/// </summary>
[TestFixture]
public sealed class ShiftScopeExpanderTests
{
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly Guid Order = Guid.NewGuid();
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Child1 = Guid.NewGuid();
    private static readonly Guid Child2 = Guid.NewGuid();
    private static readonly Guid Grandchild = Guid.NewGuid();
    private static readonly Guid OtherOrder = Guid.NewGuid();
    private static readonly Guid OtherShift = Guid.NewGuid();

    private static readonly IReadOnlyList<ShiftTreeRow> Rows =
    [
        new(Order, ShiftStatus.SealedOrder, null, null, null),
        new(Root, ShiftStatus.SplitShift, Order, null, Root),
        new(Child1, ShiftStatus.SplitShift, Order, Root, Root),
        new(Child2, ShiftStatus.SplitShift, Order, Root, Root),
        new(Grandchild, ShiftStatus.SplitShift, Order, Child1, Root),
        new(OtherOrder, ShiftStatus.SealedOrder, null, null, null),
        new(OtherShift, ShiftStatus.OriginalShift, OtherOrder, null, null),
    ];

    [Test]
    public void ReceiversOf_SealedOrder_ReachesEveryPieceOfTheOrderAndNothingElse()
    {
        ShiftScopeExpander.ReceiversOf(Order, Rows)
            .ShouldBe([Order, Root, Child1, Child2, Grandchild], ignoreOrder: true);
    }

    [Test]
    public void ReceiversOf_PlannableCopy_ReachesEveryShiftOfItsOrder()
    {
        var rows = new List<ShiftTreeRow>
        {
            new(OtherOrder, ShiftStatus.SealedOrder, null, null, null),
            new(OtherShift, ShiftStatus.OriginalShift, OtherOrder, null, null),
            new(Child1, ShiftStatus.SplitShift, OtherOrder, null, Child1),
        };

        ShiftScopeExpander.ReceiversOf(OtherShift, rows).ShouldBe([OtherShift, Child1], ignoreOrder: true);
    }

    [Test]
    public void ReceiversOf_CutPiece_ReachesOnlyItsOwnDescendants()
    {
        ShiftScopeExpander.ReceiversOf(Root, Rows).ShouldBe([Root, Child1, Child2, Grandchild], ignoreOrder: true);
        ShiftScopeExpander.ReceiversOf(Child1, Rows).ShouldBe([Child1, Grandchild], ignoreOrder: true);
    }

    [Test]
    public void ReceiversOf_UnknownShift_IsOnlyItself()
    {
        var unknown = Guid.NewGuid();

        ShiftScopeExpander.ReceiversOf(unknown, Rows).ShouldBe([unknown]);
    }

    [Test]
    public void ExpandPreferences_BlacklistOnTheOrder_ReachesEveryPiece()
    {
        var expanded = ShiftScopeExpander.ExpandPreferences(
            [new ScopedShiftPreference(Client, Order, ShiftPreferenceType.Blacklist)], Rows);

        expanded.Select(p => p.ShiftId).ShouldBe([Order, Root, Child1, Child2, Grandchild], ignoreOrder: true);
        expanded.ShouldAllBe(p => p.PreferenceType == ShiftPreferenceType.Blacklist && p.ClientId == Client);
    }

    [Test]
    public void ExpandPreferences_ExplicitEntryOnAPieceBeatsTheInheritedOne()
    {
        var expanded = ShiftScopeExpander.ExpandPreferences(
            [
                new ScopedShiftPreference(Client, Order, ShiftPreferenceType.Blacklist),
                new ScopedShiftPreference(Client, Child2, ShiftPreferenceType.Preferred),
            ],
            Rows);

        expanded.Where(p => p.ShiftId == Child2).ShouldHaveSingleItem().PreferenceType.ShouldBe(ShiftPreferenceType.Preferred);
        expanded.Single(p => p.ShiftId == Child1).PreferenceType.ShouldBe(ShiftPreferenceType.Blacklist);
    }

    [Test]
    public void ExpandPreferences_ConflictingInheritedEntries_ResolveToBlacklist()
    {
        var expanded = ShiftScopeExpander.ExpandPreferences(
            [
                new ScopedShiftPreference(Client, Order, ShiftPreferenceType.Preferred),
                new ScopedShiftPreference(Client, Child1, ShiftPreferenceType.Blacklist),
            ],
            Rows);

        expanded.Single(p => p.ShiftId == Grandchild).PreferenceType.ShouldBe(
            ShiftPreferenceType.Blacklist, "Grandchild inherits Preferred from the order and Blacklist from its parent piece.");
        expanded.Single(p => p.ShiftId == Child2).PreferenceType.ShouldBe(ShiftPreferenceType.Preferred);
    }

    [Test]
    public void ExpandPreferences_PreferenceOfAnotherClientIsNeverMerged()
    {
        var otherClient = Guid.NewGuid();

        var expanded = ShiftScopeExpander.ExpandPreferences(
            [
                new ScopedShiftPreference(Client, Order, ShiftPreferenceType.Blacklist),
                new ScopedShiftPreference(otherClient, Child1, ShiftPreferenceType.Preferred),
            ],
            Rows);

        expanded.Where(p => p.ClientId == otherClient).Select(p => p.ShiftId).ShouldBe([Child1, Grandchild], ignoreOrder: true);
        expanded.Where(p => p.ClientId == otherClient).ShouldAllBe(p => p.PreferenceType == ShiftPreferenceType.Preferred);
    }

    [Test]
    public void ExpandPreferences_PreferredOnAMidPiece_DoesNotLiftTheOrderBlacklistForItsChildren()
    {
        var expanded = ShiftScopeExpander.ExpandPreferences(
            [
                new ScopedShiftPreference(Client, Order, ShiftPreferenceType.Blacklist),
                new ScopedShiftPreference(Client, Child1, ShiftPreferenceType.Preferred),
            ],
            Rows);

        expanded.Single(p => p.ShiftId == Child1).PreferenceType.ShouldBe(
            ShiftPreferenceType.Preferred, "the explicit entry on the mid piece itself wins");
        expanded.Single(p => p.ShiftId == Grandchild).PreferenceType.ShouldBe(
            ShiftPreferenceType.Blacklist, "owner rule: Blacklist wins across all levels, not nearest-wins");
        expanded.Single(p => p.ShiftId == Child2).PreferenceType.ShouldBe(ShiftPreferenceType.Blacklist);
    }

    [Test]
    public void ReceiversOf_ParentIdCycle_Terminates()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rows = new List<ShiftTreeRow>
        {
            new(a, ShiftStatus.SplitShift, Order, b, a),
            new(b, ShiftStatus.SplitShift, Order, a, a),
        };

        ShiftScopeExpander.ReceiversOf(a, rows).ShouldBe([a, b], ignoreOrder: true);
        ShiftScopeExpander.ExpandPreferences([new ScopedShiftPreference(Client, a, ShiftPreferenceType.Blacklist)], rows)
            .Select(p => p.ShiftId).ShouldBe([a, b], ignoreOrder: true);
    }
}
