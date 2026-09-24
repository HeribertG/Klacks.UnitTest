// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The tool-less request shapes of LLMProviderRequestFactory. A recipe step and the empty-answer recovery
/// are tool-less, carry no pending-notes hint and switch thinking off; the plain ToolLess shape removes
/// the hint too but keeps the configured thinking, and an ordinary iteration request is untouched.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMProviderRequestFactoryTests
{
    private const string Message = "Create a group";
    private const string SystemPrompt = "stable";
    private const string StepInstruction = "Ask the user to confirm.";
    private const string OtherVolatileLine = "Today is Monday.";

    private static readonly string VolatileWithHint = OtherVolatileLine + "\n" + string.Format(
        CultureInfo.InvariantCulture, PendingNotesPromptConstants.HintTemplate, 2);

    private static readonly LLMModel Model = new() { ModelId = "m", ApiModelId = "api-m", MaxTokens = 1024 };

    private static List<ProviderLLMMessage> History() => new();

    [Test]
    public void RecipeStep_IsToolLessWithThinkingDisabledAndWithoutThePendingNotesHint()
    {
        var request = LLMProviderRequestFactory.RecipeStep(
            Model, Message, SystemPrompt, VolatileWithHint, StepInstruction, History());

        request.AvailableFunctions.ShouldBeEmpty();
        request.ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
        request.VolatileSystemPrompt!.ShouldNotContain(PendingNotesPromptConstants.Marker);
        request.VolatileSystemPrompt.ShouldContain(OtherVolatileLine);
        request.VolatileSystemPrompt.ShouldContain(StepInstruction);
    }

    [Test]
    public void Recovery_IsToolLessWithThinkingDisabledAndWithoutThePendingNotesHint()
    {
        var request = LLMProviderRequestFactory.Recovery(
            Model, Message, SystemPrompt, VolatileWithHint, EmptyAnswerRecoveryConstants.ToolLessRecoveryInstruction,
            History());

        request.AvailableFunctions.ShouldBeEmpty();
        request.ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
        request.VolatileSystemPrompt!.ShouldNotContain(PendingNotesPromptConstants.Marker);
        request.VolatileSystemPrompt.ShouldContain(EmptyAnswerRecoveryConstants.ToolLessRecoveryInstruction);
    }

    [Test]
    public void ToolLess_RemovesTheHintButKeepsTheConfiguredThinking()
    {
        var request = LLMProviderRequestFactory.ToolLess(Model, Message, SystemPrompt, VolatileWithHint, History());

        request.AvailableFunctions.ShouldBeEmpty();
        request.ThinkingBudgetTokens.ShouldBeNull();
        request.VolatileSystemPrompt.ShouldBe(OtherVolatileLine);
    }

    [Test]
    public void ForIteration_KeepsTheHintAndTheConfiguredThinking()
    {
        var request = LLMProviderRequestFactory.ForIteration(
            Model, Message, SystemPrompt, VolatileWithHint, History(),
            new List<LLMFunction> { new() { Name = SkillNames.ManagePendingNotes } }, toolChoice: null);

        request.ThinkingBudgetTokens.ShouldBeNull();
        request.VolatileSystemPrompt.ShouldBe(VolatileWithHint);
    }
}
