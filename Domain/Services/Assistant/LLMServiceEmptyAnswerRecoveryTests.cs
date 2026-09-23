// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Closing guard of both chat paths (EmptyAnswerRecovery). Live case: DeepSeek, after two tool iterations,
/// answered its third with exactly "[Executing function calls]" - the stand-in the loop itself had written
/// into the running history - and that text was streamed and stored as the answer. Pinned here: the echo
/// never reaches the client or the stored conversation, ONE tool-less follow-up call produces the answer,
/// a failed follow-up ends in the fixed notice, and a turn without tools or with only failed tools makes no
/// extra call. The running history now carries a neutral, non-bracketed sentence instead of the stand-in.
/// The streaming path decides from the LAST provider call only, so narration streamed beside an earlier
/// tool call neither hides an empty final answer nor gets cleared: the recovered answer is appended below
/// it, and the stored answer always equals what was streamed. The recovery call is announced with a status
/// event, counted in the turn's usage on both paths, and a recovery stream that breaks off ends with the
/// fallback notice below the part already shown.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceEmptyAnswerRecoveryTests
{
    private const string UserMessage = "Show me the employee and the groups.";
    private const string RecoveredAnswer = "Anna works in the groups Bern and Basel.";
    private const string LegacyPlaceholder = LLMLoopConstants.ExecutingFunctionCallsPlaceholder;
    private const string Narration = "Let me check that quickly.";
    private const string PartialAnswer = "Anna works in the gro";

    private const string NarrationThenRecovered =
        Narration + EmptyAnswerRecoveryConstants.AppendedAnswerSeparator + RecoveredAnswer;

    private static LLMProviderResponse[] NarrationThenEcho(params LLMProviderResponse[] afterEcho) =>
        new[]
        {
            LLMServiceTurnHarness.ToolCallWithProse(Narration, "get_employee"),
            LLMServiceTurnHarness.ToolCall("list_groups"),
            LLMServiceTurnHarness.Text(LegacyPlaceholder)
        }.Concat(afterEcho).ToArray();

    private static LLMServiceTurnHarness ScriptedTurn(bool streaming, params LLMProviderResponse[] responses)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(responses);
        return harness;
    }

    private static LLMProviderResponse[] TwoToolIterationsThenEcho(params LLMProviderResponse[] afterEcho) =>
        new[]
        {
            LLMServiceTurnHarness.ToolCall("get_employee"),
            LLMServiceTurnHarness.ToolCall("list_groups"),
            LLMServiceTurnHarness.Text(LegacyPlaceholder)
        }.Concat(afterEcho).ToArray();

    private static void TheLastRequestWasTheToolLessRecoveryCall(LLMServiceTurnHarness harness)
    {
        harness.Requests.Count.ShouldBe(4);
        var recovery = harness.Requests[^1];
        recovery.AvailableFunctions.ShouldBeEmpty();
        recovery.VolatileSystemPrompt.ShouldNotBeNull();
        recovery.VolatileSystemPrompt!.ShouldContain(EmptyAnswerRecoveryConstants.RecoveryInstruction);
        recovery.Message.ShouldBe(harness.Requests[2].Message);
    }

    [Test]
    public async Task Streaming_EchoedPlaceholderAfterTools_IsReplacedByTheRecoveryAnswer()
    {
        var harness = ScriptedTurn(streaming: true,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text(RecoveredAnswer)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(RecoveredAnswer);
        harness.PersistedAnswer.ShouldBe(RecoveredAnswer);
        TheLastRequestWasTheToolLessRecoveryCall(harness);
    }

    [Test]
    public async Task Streaming_RecoveryAlsoEchoesThePlaceholder_EndsWithTheFallbackNotice()
    {
        var harness = ScriptedTurn(streaming: true,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text(LegacyPlaceholder)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        harness.PersistedAnswer.ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        harness.Requests.Count.ShouldBe(4);
    }

    [Test]
    public async Task Streaming_RecoveryAlsoEchoesThePlaceholder_UsesTheTurnsLanguageForTheFallbackNotice()
    {
        var harness = ScriptedTurn(streaming: true,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text(LegacyPlaceholder)));
        var germanNotice = GracefulCorrectionTexts.VariantsOf(
            GracefulCorrectionTexts.EmptyAnswerFallbackNotice)["de"];

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage, language: "de"));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(germanNotice);
        harness.PersistedAnswer.ShouldBe(germanNotice);
    }

    [Test]
    public async Task Streaming_RecoveryAlsoEchoesThePlaceholder_UnknownLanguageFallsBackToEnglish()
    {
        var harness = ScriptedTurn(streaming: true,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text(LegacyPlaceholder)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage, language: "xx-XX"));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        harness.PersistedAnswer.ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
    }

    [Test]
    public async Task Streaming_NoToolsAndEmptyAnswer_MakesNoExtraCall()
    {
        var harness = ScriptedTurn(streaming: true, LLMServiceTurnHarness.Text(string.Empty));

        await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        harness.Requests.Count.ShouldBe(1);
        harness.PersistedAnswer.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Streaming_EveryToolFailed_KeepsTheStepFailedNoticeAndMakesNoExtraCall()
    {
        var harness = ScriptedTurn(streaming: true,
            LLMServiceTurnHarness.ToolCall("get_employee"), LLMServiceTurnHarness.Text(LegacyPlaceholder));
        harness.SkillsFail();

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        harness.Requests.Count.ShouldBe(2);
        LLMServiceTurnHarness.StreamedContent(chunks).ShouldStartWith(MutationGuardConstants.RecipeStepFailedNoticePrefix);
        LLMServiceTurnHarness.StreamedContent(chunks).ShouldNotContain(LegacyPlaceholder);
    }

    [Test]
    public async Task Streaming_ToolIterationHistory_CarriesTheNeutralSentenceInsteadOfTheBracketedPlaceholder()
    {
        var harness = ScriptedTurn(streaming: true,
            LLMServiceTurnHarness.ToolCall("get_employee"), LLMServiceTurnHarness.Text(RecoveredAnswer));

        await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        var assistantTurn = harness.Requests[1].ConversationHistory[^1];
        assistantTurn.Role.ShouldBe("assistant");
        assistantTurn.Content.ShouldBe("(Called tools: get_employee. Their results follow.)");
        assistantTurn.Content.ShouldNotContain("[");
    }

    [Test]
    public async Task NonStreaming_EchoedPlaceholderAfterTools_IsReplacedByTheRecoveryAnswer()
    {
        var harness = ScriptedTurn(streaming: false,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text(RecoveredAnswer)));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        response.Message.ShouldBe(RecoveredAnswer);
        harness.PersistedAnswer.ShouldBe(RecoveredAnswer);
        TheLastRequestWasTheToolLessRecoveryCall(harness);
    }

    [Test]
    public async Task NonStreaming_RecoveryReturnsNothing_EndsWithTheFallbackNotice()
    {
        var harness = ScriptedTurn(streaming: false,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text("  ")));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        response.Message.ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        harness.PersistedAnswer.ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        harness.Requests.Count.ShouldBe(4);
    }

    [Test]
    public async Task NonStreaming_RecoveryCallFails_EndsWithTheFallbackNoticeInsteadOfAnError()
    {
        var harness = ScriptedTurn(streaming: false,
            TwoToolIterationsThenEcho(new LLMProviderResponse { Success = false, Error = "invalid api key" }));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        response.Message.ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        harness.Requests.Count.ShouldBe(4);
    }

    [Test]
    public async Task NonStreaming_RecoveryReturnsNothing_UsesTheTurnsLanguageForTheFallbackNotice()
    {
        var harness = ScriptedTurn(streaming: false,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text("  ")));
        var germanNotice = GracefulCorrectionTexts.VariantsOf(
            GracefulCorrectionTexts.EmptyAnswerFallbackNotice)["de"];

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage, language: "de"));

        response.Message.ShouldBe(germanNotice);
        harness.PersistedAnswer.ShouldBe(germanNotice);
    }

    [Test]
    public async Task NonStreaming_NoToolsAndEmptyAnswer_MakesNoExtraCall()
    {
        var harness = ScriptedTurn(streaming: false, LLMServiceTurnHarness.Text(string.Empty));

        await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        harness.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task NonStreaming_OrdinaryAnswerAfterTools_IsKeptAndMakesNoExtraCall()
    {
        var harness = ScriptedTurn(streaming: false,
            LLMServiceTurnHarness.ToolCall("get_employee"), LLMServiceTurnHarness.Text(RecoveredAnswer));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        response.Message.ShouldBe(RecoveredAnswer);
        harness.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task Streaming_NarrationBesideAnEarlierToolCall_DoesNotHideTheEchoedFinalAnswer()
    {
        var harness = ScriptedTurn(streaming: true, NarrationThenEcho(LLMServiceTurnHarness.Text(RecoveredAnswer)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(NarrationThenRecovered);
        harness.PersistedAnswer.ShouldBe(LLMServiceTurnHarness.StreamedContent(chunks));
        TheLastRequestWasTheToolLessRecoveryCall(harness);
    }

    [Test]
    public async Task Streaming_NonStreamingProvider_NarrationThenEcho_RecoversAndKeepsTheNarration()
    {
        var harness = ScriptedTurn(streaming: false, NarrationThenEcho(LLMServiceTurnHarness.Text(RecoveredAnswer)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(NarrationThenRecovered);
        harness.PersistedAnswer.ShouldBe(LLMServiceTurnHarness.StreamedContent(chunks));
        TheLastRequestWasTheToolLessRecoveryCall(harness);
    }

    [Test]
    public async Task Streaming_OrdinaryFinalAnswerAfterNarration_MakesNoExtraCall()
    {
        var harness = ScriptedTurn(streaming: true,
            LLMServiceTurnHarness.ToolCallWithProse(Narration, "get_employee"),
            LLMServiceTurnHarness.Text(RecoveredAnswer));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        harness.Requests.Count.ShouldBe(2);
        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(Narration + RecoveredAnswer);
        harness.PersistedAnswer.ShouldBe(Narration + RecoveredAnswer);
    }

    [Test]
    public async Task Streaming_RecoveryCall_IsAnnouncedWithTheCallingModelStatusBeforeItsContent()
    {
        var harness = ScriptedTurn(streaming: true,
            TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text(RecoveredAnswer)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        var lastFunctionResult = chunks.FindLastIndex(c => c.Type == SseChunkType.FunctionResult);
        var recoveryStatus = chunks.FindLastIndex(
            c => c.Type == SseChunkType.Status && c.Stage == SseStatusStages.CallingModel);
        var firstRecoveredContent = chunks.FindIndex(c => c.Type == SseChunkType.Content);
        chunks.Count(c => c.Type == SseChunkType.Status && c.Stage == SseStatusStages.CallingModel).ShouldBe(4);
        recoveryStatus.ShouldBeGreaterThan(lastFunctionResult);
        firstRecoveredContent.ShouldBeGreaterThan(recoveryStatus);
    }

    [Test]
    public async Task Streaming_RecoveryStreamBreaksOffMidAnswer_KeepsTheShownPartAndAppendsTheFallbackNotice()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(TwoToolIterationsThenEcho(harness.StreamBreaksOffAfter(PartialAnswer)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        var expected = PartialAnswer + EmptyAnswerRecoveryConstants.AppendedAnswerSeparator
            + EmptyAnswerRecoveryConstants.FallbackNotice;
        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(expected);
        harness.PersistedAnswer.ShouldBe(expected);
        chunks.ShouldNotContain(c => c.Type == SseChunkType.Error);
    }

    [Test]
    public async Task Streaming_RecoveryStreamFailsBeforeAnyContent_AnswersWithTheFallbackNoticeOnly()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(TwoToolIterationsThenEcho(harness.StreamBreaksOffAfter(string.Empty)));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        harness.PersistedAnswer.ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RecoveryCall_UsageIsAddedToTheTurnTotals(bool streaming)
    {
        var harness = ScriptedTurn(streaming,
            LLMServiceTurnHarness.WithInputTokens(LLMServiceTurnHarness.ToolCall("get_employee"), 1),
            LLMServiceTurnHarness.WithInputTokens(LLMServiceTurnHarness.ToolCall("list_groups"), 10),
            LLMServiceTurnHarness.WithInputTokens(LLMServiceTurnHarness.Text(LegacyPlaceholder), 100),
            LLMServiceTurnHarness.WithInputTokens(LLMServiceTurnHarness.Text(RecoveredAnswer), 1000));

        if (streaming)
        {
            await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));
        }
        else
        {
            await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));
        }

        harness.Requests.Count.ShouldBe(4);
        harness.TrackedUsage.ShouldNotBeNull();
        harness.TrackedUsage!.InputTokens.ShouldBe(1111);
    }

    [Test]
    public void NoRecovery_WhenTheTurnEndedOnAUiPassthroughBatch()
    {
        var calls = new List<LLMFunctionCall> { new() { FunctionName = SkillNames.NavigateTo, Success = true } };

        EmptyAnswerRecovery.NeedsRecovery(string.Empty, calls, endedOnUiPassthrough: () => true).ShouldBeFalse();
        EmptyAnswerRecovery.NeedsRecovery(string.Empty, calls, endedOnUiPassthrough: () => false).ShouldBeTrue();
    }
}
