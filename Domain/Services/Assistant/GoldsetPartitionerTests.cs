// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the train/holdout split. It has to be a pure function of the item id and nothing else: the
/// seeder and the learning loop compute it independently, and if they ever disagreed a training item
/// would end up in the exam the learner is judged by.
/// </summary>
namespace Klacks.UnitTest.Domain.Services.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetPartitionerTests
{
    [Test]
    public void Resolve_IsStableForTheSameId()
    {
        var first = GoldsetPartitioner.Resolve("ts-011");
        var second = GoldsetPartitioner.Resolve("ts-011");

        second.ShouldBe(first);
    }

    [Test]
    public void Resolve_OnlyEverReturnsTrainOrHoldout()
    {
        var values = Enumerable.Range(0, 500)
            .Select(i => GoldsetPartitioner.Resolve($"ts-{i:D4}"))
            .Distinct()
            .ToList();

        values.ShouldBeSubsetOf([GoldenCasePartitions.Train, GoldenCasePartitions.Holdout]);
    }

    [Test]
    public void Resolve_SplitsRoughlySeventyThirty()
    {
        var ids = Enumerable.Range(0, 2000).Select(i => $"item-{i}").ToList();

        var train = ids.Count(id => GoldsetPartitioner.Resolve(id) == GoldenCasePartitions.Train);

        (train / (double)ids.Count).ShouldBeInRange(0.65, 0.75);
    }

    // Buckets computed from the FNV-1a implementation over the UTF-8 bytes of the id: "ts-001" lands in
    // 74 (holdout), "TS-001" and "ts-001 " both in 18 (train). Pinning the three exact verdicts is what
    // makes this a regression test - asserting only that two ids differ passes 42 % of the time by
    // coincidence, whatever the hash does.
    [Test]
    public void Resolve_PinsThePartitionOfCaseAndWhitespaceVariants()
    {
        GoldsetPartitioner.Resolve("ts-001").ShouldBe(GoldenCasePartitions.Holdout);
        GoldsetPartitioner.Resolve("TS-001").ShouldBe(GoldenCasePartitions.Train);
        GoldsetPartitioner.Resolve("ts-001 ").ShouldBe(GoldenCasePartitions.Train);
    }

    [Test]
    public void IsHoldout_AgreesWithResolve()
    {
        GoldsetPartitioner.IsHoldout("ts-011")
            .ShouldBe(GoldsetPartitioner.Resolve("ts-011") == GoldenCasePartitions.Holdout);
    }

    [Test]
    public void Resolve_EmptyId_IsHoldout()
    {
        GoldsetPartitioner.Resolve(string.Empty).ShouldBe(GoldenCasePartitions.Holdout);
    }
}
