// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GroupValidFromPropagation: a lowered group start is carried up the Parent chain so every ancestor starts
/// with the earliest of its own start and its children's lowered starts; a start is never raised, groups that already
/// start early enough and unrelated groups are left alone, and several lowered children of one parent give the parent the
/// earliest of them.
/// </summary>

using Klacks.Api.Domain.Services.Groups;

namespace Klacks.UnitTest.Domain.Services.Groups;

[TestFixture]
public class GroupValidFromPropagationTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Mid = Guid.NewGuid();
    private static readonly Guid LeafA = Guid.NewGuid();
    private static readonly Guid LeafB = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateTime Late = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static List<(Guid Id, Guid? Parent, DateTime ValidFrom)> Tree(DateTime rootStart, DateTime midStart) =>
    [
        (Root, null, rootStart),
        (Mid, Root, midStart),
        (LeafA, Mid, Late),
        (LeafB, Mid, Late),
        (Other, Root, Late)
    ];

    [Test]
    public void ALoweredLeaf_LowersEveryAncestorAlongTheParentChain()
    {
        var result = GroupValidFromPropagation.Resolve(Tree(Late, Late), new Dictionary<Guid, DateTime> { [LeafA] = Utc(2024, 2, 1) });

        result[LeafA].ShouldBe(Utc(2024, 2, 1));
        result[Mid].ShouldBe(Utc(2024, 2, 1));
        result[Root].ShouldBe(Utc(2024, 2, 1));
        result.ContainsKey(LeafB).ShouldBeFalse();
        result.ContainsKey(Other).ShouldBeFalse();
    }

    [Test]
    public void AnAncestorThatAlreadyStartsEarlier_IsLeftAlone()
    {
        var result = GroupValidFromPropagation.Resolve(
            Tree(Utc(2020, 1, 1), Late), new Dictionary<Guid, DateTime> { [LeafA] = Utc(2024, 2, 1) });

        result.ContainsKey(Root).ShouldBeFalse();
        result[Mid].ShouldBe(Utc(2024, 2, 1));
    }

    [Test]
    public void AStartIsNeverRaised()
    {
        var result = GroupValidFromPropagation.Resolve(Tree(Late, Late), new Dictionary<Guid, DateTime> { [LeafA] = Utc(2030, 1, 1) });

        result.ContainsKey(LeafA).ShouldBeFalse();
        result.ContainsKey(Mid).ShouldBeFalse();
    }

    [Test]
    public void TwoLoweredChildren_GiveTheParentTheEarlierOfThem()
    {
        var result = GroupValidFromPropagation.Resolve(
            Tree(Late, Late),
            new Dictionary<Guid, DateTime> { [LeafA] = Utc(2024, 2, 1), [LeafB] = Utc(2021, 5, 1) });

        result[Mid].ShouldBe(Utc(2021, 5, 1));
        result[Root].ShouldBe(Utc(2021, 5, 1));
        result[LeafA].ShouldBe(Utc(2024, 2, 1));
    }

    [Test]
    public void AGroupOutsideTheTree_IsIgnored()
    {
        var result = GroupValidFromPropagation.Resolve(Tree(Late, Late), new Dictionary<Guid, DateTime> { [Guid.NewGuid()] = Utc(2024, 2, 1) });

        result.ShouldBeEmpty();
    }
}
