// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Domain.Common;

[TestFixture]
public class SeededSampleTests
{
    private static readonly IReadOnlyList<int> Items = Enumerable.Range(1, 50).ToList();

    [Test]
    public void SameSeed_YieldsTheSameSelection()
    {
        var first = SeededSample.Take(Items, 10, 1234);
        var second = SeededSample.Take(Items, 10, 1234);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Selection_HasTheRequestedSize_WithoutDuplicates_FromTheCandidates()
    {
        var sample = SeededSample.Take(Items, 10, 99);

        Assert.That(sample, Has.Count.EqualTo(10));
        Assert.That(sample, Is.Unique);
        Assert.That(sample, Is.SubsetOf(Items));
    }

    [Test]
    public void DifferentSeeds_DoNotAlwaysPickTheFirstItems()
    {
        var firstTen = Items.Take(10).ToList();

        var samples = Enumerable.Range(1, 5).Select(seed => SeededSample.Take(Items, 10, seed)).ToList();

        Assert.That(samples.Any(sample => !sample.OrderBy(i => i).SequenceEqual(firstTen)), Is.True);
    }

    [Test]
    public void CountAboveTheCandidates_ReturnsAllCandidates()
    {
        var sample = SeededSample.Take(Items, 500, 7);

        Assert.That(sample, Is.EquivalentTo(Items));
    }

    [Test]
    public void StableSeed_DependsOnlyOnTheParts()
    {
        Assert.That(SeededSample.StableSeed("a", null, "c"), Is.EqualTo(SeededSample.StableSeed("a", null, "c")));
        Assert.That(SeededSample.StableSeed("a", "b"), Is.Not.EqualTo(SeededSample.StableSeed("a", "c")));
        Assert.That(SeededSample.StableSeed("ab", "c"), Is.Not.EqualTo(SeededSample.StableSeed("a", "bc")));
        Assert.That(SeededSample.StableSeed("x"), Is.GreaterThanOrEqualTo(0));
    }
}
