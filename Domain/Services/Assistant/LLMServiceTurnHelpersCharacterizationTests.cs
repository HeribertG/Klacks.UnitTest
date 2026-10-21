// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for the small LLMService helpers both chat loops share and no other test pinned:
/// the volatile-prompt combination, the turn clock and correlation id used by status chunks and logs, and the
/// in-loop fitting of the running history to the turn's history budget. Written against the unsplit
/// LLMService so the split into collaborators can be checked against the same expectations.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Logging;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Shouldly;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceTurnHelpersCharacterizationTests
{
    private const string BasePrompt = "base prompt";
    private const string Note = "per-turn note";
    private const string SystemRole = "system";
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    [TestCase(null, null, "")]
    [TestCase(null, "", "")]
    [TestCase(BasePrompt, null, BasePrompt)]
    [TestCase(BasePrompt, "", BasePrompt)]
    [TestCase(null, Note, Note)]
    [TestCase("", Note, Note)]
    public void CombineVolatile_WithAMissingPart_ReturnsTheOtherPart(string? basePrompt, string? note, string expected)
    {
        LLMService.CombineVolatile(basePrompt, note).ShouldBe(expected);
    }

    [Test]
    public void CombineVolatile_WithBothParts_SeparatesThemByABlankLine()
    {
        LLMService.CombineVolatile(BasePrompt, Note).ShouldBe($"{BasePrompt}\n\n{Note}");
    }

    [Test]
    public void ElapsedMsFor_WithoutATurnClock_IsNull()
    {
        LLMService.ElapsedMsFor(new LLMContext { TurnStartTimestamp = null }).ShouldBeNull();
    }

    [Test]
    public void ElapsedMsFor_WithATurnClock_MeasuresFromThatClock()
    {
        var context = new LLMContext { TurnStartTimestamp = Stopwatch.GetTimestamp() };

        var elapsed = LLMService.ElapsedMsFor(context);

        elapsed.ShouldNotBeNull();
        elapsed.Value.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void TurnCorrelationFor_WithATurnId_FormatsThatTurnId()
    {
        var turnId = Guid.NewGuid();

        LLMService.TurnCorrelationFor(new LLMContext { TurnId = turnId }).ShouldBe(TurnCorrelation.Format(turnId));
    }

    [Test]
    public void TurnCorrelationFor_WithoutATurnId_FallsBackToTheAmbientCorrelation()
    {
        LLMService.TurnCorrelationFor(new LLMContext { TurnId = null }).ShouldBe(TurnCorrelation.CurrentOrNone);
    }

    [Test]
    public void FitRunningHistoryToBudget_UnderBudget_KeepsEveryMessage()
    {
        var history = new List<ProviderLLMMessage> { Message(UserRole, 40), Message(AssistantRole, 40) };

        LLMService.FitRunningHistoryToBudget(history, Text(40), 1_000);

        history.Count.ShouldBe(2);
    }

    [Test]
    public void FitRunningHistoryToBudget_OverBudget_DropsTheOldestMessagesFirst()
    {
        var oldest = Message(UserRole, 400);
        var middle = Message(AssistantRole, 400);
        var newest = Message(UserRole, 400);
        var history = new List<ProviderLLMMessage> { oldest, middle, newest };

        LLMService.FitRunningHistoryToBudget(history, Text(400), 250);

        history.ShouldBe(new[] { newest });
    }

    [Test]
    public void FitRunningHistoryToBudget_KeepsALeadingSystemMessage()
    {
        var summary = Message(SystemRole, 400);
        var older = Message(UserRole, 400);
        var newer = Message(AssistantRole, 400);
        var history = new List<ProviderLLMMessage> { summary, older, newer };

        LLMService.FitRunningHistoryToBudget(history, Text(400), 250);

        history.ShouldBe(new[] { summary });
    }

    [Test]
    public void FitRunningHistoryToBudget_StopsWhenOnlyTheSystemMessageIsLeft()
    {
        var summary = Message(SystemRole, 4_000);
        var history = new List<ProviderLLMMessage> { summary };

        LLMService.FitRunningHistoryToBudget(history, Text(4_000), 10);

        history.ShouldBe(new[] { summary });
    }

    [Test]
    public void FitRunningHistoryToBudget_CountsTheNextMessageAgainstTheBudget()
    {
        var older = Message(UserRole, 400);
        var newer = Message(AssistantRole, 400);
        var history = new List<ProviderLLMMessage> { older, newer };

        LLMService.FitRunningHistoryToBudget(history, Text(800), 300);

        history.ShouldBe(new[] { newer });
    }

    private static ProviderLLMMessage Message(string role, int characters) =>
        new() { Role = role, Content = Text(characters) };

    private static string Text(int characters) => new('x', characters);
}
