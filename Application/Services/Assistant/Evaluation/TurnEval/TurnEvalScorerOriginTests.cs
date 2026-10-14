// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the per-origin report: every origin present gets its own dimensions computed from its items only,
/// origins without items are absent, and the whole-run aggregate is unchanged.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalScorerOriginTests
{
    private const string ExpectedTool = "add_client_note";
    private const string WrongTool = "create_group";

    private static TurnEvalItemResult Scored(string id, string? source, string chosenTool)
    {
        var item = new TurnGoldsetItem { Id = id, Message = id, ExpectedTool = ExpectedTool, Source = source };
        var replay = new TurnReplayResult
        {
            Success = true,
            ChosenTool = chosenTool,
            AvailableToolNames = [ExpectedTool, WrongTool]
        };
        replay.Steps.Add(new TurnReplayStep { Tool = chosenTool });
        return TurnEvalScorer.ScoreItem(item, replay);
    }

    [Test]
    public void AggregateByOrigin_SplitsTheCoreDimensionsPerOrigin()
    {
        var items = new List<TurnEvalItemResult>
        {
            Scored("h-1", "curated", ExpectedTool),
            Scored("h-2", "curated", ExpectedTool),
            Scored("s-1", "w0.5-seed-coverage", WrongTool),
            Scored("s-2", "paraphrase", ExpectedTool),
            Scored("s-3", "translation", WrongTool),
            Scored("s-4", "w0.5-recipe", WrongTool)
        };

        var byOrigin = TurnEvalScorer.AggregateByOrigin(items);

        byOrigin.Keys.ShouldBe([TurnGoldsetOrigin.Handwritten, TurnGoldsetOrigin.Synthetic]);
        byOrigin[TurnGoldsetOrigin.Handwritten].ToolAccuracy.ShouldBe(1.0);
        byOrigin[TurnGoldsetOrigin.Handwritten].ItemsTotal.ShouldBe(2);
        byOrigin[TurnGoldsetOrigin.Handwritten].ReachedHit.ShouldBe(1.0);
        byOrigin[TurnGoldsetOrigin.Synthetic].ToolAccuracy.ShouldBe(0.25);
        byOrigin[TurnGoldsetOrigin.Synthetic].ItemsTotal.ShouldBe(4);
        byOrigin[TurnGoldsetOrigin.Synthetic].SelectionHit.ShouldBe(0.25);
        byOrigin.Values.Sum(d => d.ItemsTotal).ShouldBe(items.Count);
    }

    [Test]
    public void AggregateByOrigin_ReportsAnUnmarkedItemUnderUnknownAndNeverAsHandwritten()
    {
        var byOrigin = TurnEvalScorer.AggregateByOrigin([Scored("u-1", null, ExpectedTool)]);

        byOrigin.Keys.ShouldBe([TurnGoldsetOrigin.Unknown]);
    }

    [Test]
    public void AggregateByOrigin_OfNoItems_IsEmpty()
    {
        TurnEvalScorer.AggregateByOrigin([]).ShouldBeEmpty();
    }

    [Test]
    public void WholeRunAggregate_IsTheMixOfBothOrigins()
    {
        var items = new List<TurnEvalItemResult>
        {
            Scored("h-1", "curated", ExpectedTool),
            Scored("s-1", "w0.5-seed-coverage", WrongTool)
        };

        TurnEvalScorer.Aggregate(items).ToolAccuracy.ShouldBe(0.5);
    }
}
