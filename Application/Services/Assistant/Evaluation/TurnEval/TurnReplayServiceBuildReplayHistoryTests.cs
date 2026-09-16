// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// BuildReplayHistory must behave identically across providers: Anthropic drops whitespace-only
/// history messages while OpenAI-compatible providers do not, so a blank assistant excerpt must never
/// reach the wire as a message in the first place.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnReplayServiceBuildReplayHistoryTests
{
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    [Test]
    public void ItemWithoutPreviousTurn_ReturnsAnEmptyHistory()
    {
        var item = new TurnGoldsetItem { Message = "Hallo" };

        var history = TurnReplayService.BuildReplayHistory(item);

        history.ShouldBeEmpty();
    }

    [Test]
    public void ItemWithAnAssistantExcerpt_ReturnsUserThenAssistant()
    {
        var item = new TurnGoldsetItem
        {
            Message = "Nein, ich meinte alle Mitarbeitenden.",
            PreviousTurn = new TurnGoldsetPreviousTurn
            {
                Message = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.",
                CalledSkill = "find_customer_candidates",
                AssistantAnswerExcerpt = "Ich habe nach Kunden gesucht."
            }
        };

        var history = TurnReplayService.BuildReplayHistory(item);

        history.Count.ShouldBe(2);
        history[0].Role.ShouldBe(UserRole);
        history[0].Content.ShouldBe(item.PreviousTurn.Message);
        history[1].Role.ShouldBe(AssistantRole);
        history[1].Content.ShouldBe(item.PreviousTurn.AssistantAnswerExcerpt);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ItemWithABlankAssistantExcerpt_OmitsTheAssistantEntry(string? excerpt)
    {
        var item = new TurnGoldsetItem
        {
            Message = "Nein, ich meinte alle Mitarbeitenden.",
            PreviousTurn = new TurnGoldsetPreviousTurn
            {
                Message = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.",
                CalledSkill = "find_customer_candidates",
                AssistantAnswerExcerpt = excerpt
            }
        };

        var history = TurnReplayService.BuildReplayHistory(item);

        history.Count.ShouldBe(1);
        history[0].Role.ShouldBe(UserRole);
    }
}
