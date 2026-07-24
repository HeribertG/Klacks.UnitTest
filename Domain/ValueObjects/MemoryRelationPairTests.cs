// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MemoryRelationPair.Canonical: an undirected memory edge must resolve to the same
/// pair regardless of the order the two memory ids are passed in.
/// </summary>

using Klacks.Api.Domain.ValueObjects;

namespace Klacks.UnitTest.Domain.ValueObjects;

[TestFixture]
public class MemoryRelationPairTests
{
    [Test]
    public void Canonical_SameForBothOrders()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var forward = MemoryRelationPair.Canonical(idA, idB);
        var backward = MemoryRelationPair.Canonical(idB, idA);

        forward.ShouldBe(backward);
    }

    [Test]
    public void Canonical_SmallerIdIsAlwaysMemoryA()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expectedFirst = idA.CompareTo(idB) <= 0 ? idA : idB;
        var expectedSecond = idA.CompareTo(idB) <= 0 ? idB : idA;

        var pair = MemoryRelationPair.Canonical(idB, idA);

        pair.MemoryAId.ShouldBe(expectedFirst);
        pair.MemoryBId.ShouldBe(expectedSecond);
    }
}
