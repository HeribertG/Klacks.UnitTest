// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the additive MissKindCounts dimension and the stable persisted names of EvalMissKind: the counts stay
/// null (and out of the JSON) while nothing is classified, and no other dimension moves.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalScorerMissKindTests
{
    private const string CountsProperty = "MissKindCounts";
    private const int KindCount = 11;
    private const int ScorerVersionBeforeMissKind = 6;

    private static TurnEvalItemResult Item(EvalMissKind? kind, bool selectionHit = true) => new()
    {
        ItemId = "item",
        ExpectedTool = "tool",
        ToolHit = selectionHit,
        SelectionHit = selectionHit,
        RetrievalHit = true,
        MissKind = kind
    };

    [Test]
    public void Aggregate_WithoutClassifiedItems_LeavesTheCountsNull()
    {
        var dimensions = TurnEvalScorer.Aggregate([Item(null), Item(null)]);

        dimensions.MissKindCounts.ShouldBeNull();
        JsonSerializer.Serialize(dimensions).ShouldNotContain(CountsProperty);
    }

    [Test]
    public void Aggregate_OfAnEmptyList_LeavesTheCountsNull()
    {
        TurnEvalScorer.Aggregate([]).MissKindCounts.ShouldBeNull();
    }

    [Test]
    public void Aggregate_CountsEveryKindUnderItsWireNameInEnumOrder()
    {
        var dimensions = TurnEvalScorer.Aggregate(
        [
            Item(EvalMissKind.WrongRead, false),
            Item(EvalMissKind.Correct),
            Item(EvalMissKind.WrongRead, false),
            Item(EvalMissKind.NotApplicable)
        ]);

        dimensions.MissKindCounts.ShouldNotBeNull();
        dimensions.MissKindCounts!.Keys.ToList().ShouldBe(
            [EvalMissKindNames.Correct, EvalMissKindNames.WrongRead, EvalMissKindNames.NotApplicable]);
        dimensions.MissKindCounts[EvalMissKindNames.WrongRead].ShouldBe(2);
        dimensions.MissKindCounts[EvalMissKindNames.Correct].ShouldBe(1);
        JsonSerializer.Serialize(dimensions).ShouldContain(CountsProperty);
    }

    [Test]
    public void Aggregate_ClassifiedAndUnclassified_OnlyDifferInTheCounts()
    {
        var unclassified = new[] { Item(null), Item(null, false) };
        var classified = new[] { Item(EvalMissKind.Correct), Item(EvalMissKind.WrongRead, false) };

        var before = TurnEvalScorer.Aggregate(unclassified);
        var after = TurnEvalScorer.Aggregate(classified);

        after.ToolAccuracy.ShouldBe(before.ToolAccuracy);
        after.SelectionHit.ShouldBe(before.SelectionHit);
        after.RetrievalHit.ShouldBe(before.RetrievalHit);
        (after with { MissKindCounts = null }).ShouldBe(before);
        TurnEvalScorer.ComputeComposite(after).ShouldBe(TurnEvalScorer.ComputeComposite(before));
    }

    [Test]
    public void DimensionsJson_OfAnOlderRun_DeserializesWithoutCounts()
    {
        const string olderRun = """{"ToolAccuracy":1,"AvgLatencyMs":0,"TotalCost":0,"ItemsTotal":1,"ItemsPassed":1,"ItemsExcluded":0,"ItemsErrored":0}""";

        var dimensions = JsonSerializer.Deserialize<TurnEvalDimensions>(olderRun);

        dimensions.ShouldNotBeNull();
        dimensions!.MissKindCounts.ShouldBeNull();
    }

    [Test]
    public void ScorerVersion_IsNotBumped()
    {
        TurnEvalScorer.ScorerVersion.ShouldBe(ScorerVersionBeforeMissKind);
    }

    [Test]
    public void WireNames_AreDistinctSnakeCaseAndCoverEveryKind()
    {
        var names = Enum.GetValues<EvalMissKind>().Select(EvalMissKindNames.Of).ToList();

        names.Count.ShouldBe(KindCount);
        names.Distinct().Count().ShouldBe(names.Count);
        names.ShouldAllBe(name => name.All(c => char.IsAsciiLetterLower(c) || c == '_'));
        names.ShouldAllBe(name => name.Length <= TurnEvalDefaults.MissKindMaxLength);
    }
}
