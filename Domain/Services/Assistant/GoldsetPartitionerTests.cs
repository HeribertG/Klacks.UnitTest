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

    // By hash alone roughly 30 of these ids would land in the holdout half, so all-train proves the rule.
    [Test]
    public void Resolve_AParaphraseId_IsAlwaysTrain()
    {
        var ids = Enumerable.Range(1, 300)
            .Select(i => $"{TurnEvalDefaults.ParaphraseItemIdPrefix}ts-{i:D3}-1")
            .ToList();

        ids.ShouldAllBe(id => GoldsetPartitioner.Resolve(id) == GoldenCasePartitions.Train);
    }

    [Test]
    public void Resolve_AnOrdinaryIdKeepsItsHashPartition()
    {
        GoldsetPartitioner.Resolve("ts-001").ShouldBe(GoldenCasePartitions.Holdout);
    }

    // Hashing the translation id itself would disagree with the source for roughly 40 % of these pairs, so
    // agreement across 300 sources times four locales (one of them hyphenated) proves the inheritance rule.
    [Test]
    public void Resolve_ATranslationId_InheritsThePartitionOfItsSource()
    {
        string[] locales = ["ja", "ar", "zh-CN", "en"];
        var sources = Enumerable.Range(1, 300).Select(i => $"ts-{i:D3}").ToList();

        sources.Select(GoldsetPartitioner.Resolve).Distinct().Count().ShouldBe(2);

        foreach (var source in sources)
        {
            foreach (var locale in locales)
            {
                GoldsetPartitioner.Resolve(GoldsetTranslationId.Compose(locale, source))
                    .ShouldBe(GoldsetPartitioner.Resolve(source), $"{locale}/{source}");
            }
        }
    }

    [Test]
    public void Resolve_TheTranslationOfAHoldoutSource_StaysHoldout()
    {
        GoldsetPartitioner.Resolve("i18n-ja--ts-001").ShouldBe(GoldenCasePartitions.Holdout);
        GoldsetPartitioner.Resolve("i18n-zh-CN--ts-001").ShouldBe(GoldenCasePartitions.Holdout);
    }

    [Test]
    public void Resolve_TheTranslationOfATrainSource_IsTrain()
    {
        var trainSource = Enumerable.Range(1, 50).Select(i => $"ts-{i:D3}").First(GoldsetPartitioner.IsTrain);

        GoldsetPartitioner.Resolve(GoldsetTranslationId.Compose("ar", trainSource)).ShouldBe(GoldenCasePartitions.Train);
    }

    // A malformed translation id names no source whose partition it could inherit; by hash alone about 70 %
    // of these would land in train, so all-holdout proves they never become training data.
    [Test]
    public void Resolve_AMalformedTranslationId_IsHoldout()
    {
        var malformed = Enumerable.Range(1, 300)
            .SelectMany(i => new[]
            {
                $"{TurnEvalDefaults.I18nItemIdPrefix}ja-ts-{i:D3}",
                $"{TurnEvalDefaults.I18nItemIdPrefix}ja{GoldsetTranslationId.SourceSeparator}",
                $"{TurnEvalDefaults.I18nItemIdPrefix}{GoldsetTranslationId.SourceSeparator}ts-{i:D3}"
            })
            .ToList();

        malformed.ShouldAllBe(id => GoldsetPartitioner.Resolve(id) == GoldenCasePartitions.Holdout);
    }
}
