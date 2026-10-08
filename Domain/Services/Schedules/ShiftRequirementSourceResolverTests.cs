// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// Nearest link with own requirement rows wins along shift -> cut ancestors -> plannable copy -> sealed order.
/// Tree as the cut dialog leaves it: sealed order A, former plannable copy R (top-level piece), C1 and C2 cut from R,
/// G cut from C1.
/// </summary>
[TestFixture]
public sealed class ShiftRequirementSourceResolverTests
{
    private static readonly Guid Order = Guid.NewGuid();
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Child1 = Guid.NewGuid();
    private static readonly Guid Child2 = Guid.NewGuid();
    private static readonly Guid Grandchild = Guid.NewGuid();

    private static readonly IReadOnlyList<ShiftTreeRow> CutTree =
    [
        new(Order, ShiftStatus.SealedOrder, null, null, null),
        new(Root, ShiftStatus.SplitShift, Order, null, Root),
        new(Child1, ShiftStatus.SplitShift, Order, Root, Root),
        new(Child2, ShiftStatus.SplitShift, Order, Root, Root),
        new(Grandchild, ShiftStatus.SplitShift, Order, Child1, Root),
    ];

    private static IReadOnlyDictionary<Guid, Guid> Resolve(
        IReadOnlyCollection<Guid> shiftIds, IReadOnlyCollection<ShiftTreeRow> rows, params Guid[] shiftsWithOwnRows)
        => ShiftRequirementSourceResolver.ResolveSources(shiftIds, rows, shiftsWithOwnRows.ToHashSet());

    [Test]
    public void PieceWithoutOwnRows_TakesTheRowsOfTheFormerCopy_NotTheOrder()
    {
        var sources = Resolve([Child1, Grandchild], CutTree, Order, Root);

        sources[Child1].ShouldBe(Root, "the former copy has its own rows (R), so the order's Q no longer applies");
        sources[Grandchild].ShouldBe(Root);
    }

    [Test]
    public void FormerCopyWithoutRows_FallsBackToTheOrder()
    {
        var sources = Resolve([Root, Child2, Grandchild], CutTree, Order);

        sources[Root].ShouldBe(Order);
        sources[Child2].ShouldBe(Order);
        sources[Grandchild].ShouldBe(Order);
    }

    [Test]
    public void PieceWithOwnRows_UsesOnlyItsOwnRows()
    {
        var sources = Resolve([Child1, Grandchild, Child2], CutTree, Order, Root, Child1);

        sources[Child1].ShouldBe(Child1);
        sources[Grandchild].ShouldBe(Child1, "the nearest cut ancestor with own rows wins");
        sources[Child2].ShouldBe(Root);
    }

    [Test]
    public void UncutPlannableCopy_IsTheLinkBetweenTopLevelPiecesAndTheOrder()
    {
        var copy = Guid.NewGuid();
        var piece = Guid.NewGuid();
        var rows = new List<ShiftTreeRow>
        {
            new(Order, ShiftStatus.SealedOrder, null, null, null),
            new(copy, ShiftStatus.OriginalShift, Order, null, null),
            new(piece, ShiftStatus.SplitShift, Order, null, piece),
        };

        Resolve([copy, piece], rows, Order, copy).ShouldBe(new Dictionary<Guid, Guid> { [copy] = copy, [piece] = copy });
        Resolve([copy, piece], rows, Order).ShouldBe(new Dictionary<Guid, Guid> { [copy] = Order, [piece] = Order });
    }

    [Test]
    public void NoLinkWithRows_HasNoSource()
    {
        Resolve([Root, Child1], CutTree).ShouldBeEmpty();
    }

    [Test]
    public void ScenarioCloneWithoutClonedOrder_SeesOnlyItsOwnAndAncestorRows()
    {
        var clonePiece = Guid.NewGuid();
        var cloneRoot = Guid.NewGuid();
        var rows = new List<ShiftTreeRow>
        {
            new(cloneRoot, ShiftStatus.SplitShift, null, null, cloneRoot),
            new(clonePiece, ShiftStatus.SplitShift, null, cloneRoot, cloneRoot),
        };

        Resolve([clonePiece], rows).ShouldBeEmpty(
            "known limit: a clone whose order was not cloned has OriginalId = null and loses the order's requirement");
        Resolve([clonePiece], rows, cloneRoot)[clonePiece].ShouldBe(cloneRoot);
    }

    [Test]
    public void ParentIdCycle_Terminates_AndStillReachesTheOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rows = new List<ShiftTreeRow>
        {
            new(Order, ShiftStatus.SealedOrder, null, null, null),
            new(a, ShiftStatus.SplitShift, Order, b, a),
            new(b, ShiftStatus.SplitShift, Order, a, a),
        };

        var sources = Resolve([a, b], rows, Order);

        sources[a].ShouldBe(Order);
        sources[b].ShouldBe(Order);
    }

    [Test]
    public void UnknownShiftWithOwnRows_IsItsOwnSource()
    {
        var unknown = Guid.NewGuid();

        Resolve([unknown], CutTree, unknown)[unknown].ShouldBe(unknown);
    }
}
