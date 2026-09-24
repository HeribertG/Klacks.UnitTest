// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Closing guard of both chat paths (EmptyAnswerRecovery). Live case: DeepSeek, after two tool iterations,
/// answered its third with exactly "[Executing function calls]" - the stand-in the loop itself had written
/// into the running history - and that text was streamed and stored as the answer. Pinned here: the echo
/// never reaches the client or the stored conversation, ONE tool-less follow-up call produces the answer,
/// a failed follow-up ends in the fixed notice, and a turn with only failed tools makes no extra call. A turn
/// without any tool call that ends empty (a reasoning model that deliberated without writing content,
/// 2026-09-24) is recovered too, with the tool-less instruction and against the user's message; a recipe
/// confirmation or ask step never is, because its reply is already deterministic. Every recovery call runs
/// with thinking disabled and without the tool-directed pending-notes hint. When such a turn without tool
/// calls cannot be recovered, it ends with the no-action notice ("nothing was executed"), never with the
/// tools-ran notice; a non-streamed recovery answer of that turn that claims a completed action is replaced
/// by the same notice, a clarifying question is kept, and after a nudge the recovery history no longer
/// repeats the user's message.
/// The running history now carries a neutral, non-bracketed sentence instead of the stand-in.
/// The streaming path decides from the LAST provider call only, so narration streamed beside an earlier
/// tool call neither hides an empty final answer nor gets cleared: the recovered answer is appended below
/// it, and the stored answer always equals what was streamed. The recovery call is announced with a status
/// event, counted in the turn's usage on both paths, and a recovery stream that breaks off ends with the
/// fallback notice below the part already shown.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;
using ProviderMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceEmptyAnswerRecoveryTests
{
    private const string UserMessage = "Show me the employee and the groups.";
    private const string RecoveredAnswer = "Anna works in the groups Bern and Basel.";
    private const string LegacyPlaceholder = LLMLoopConstants.ExecutingFunctionCallsPlaceholder;
    private const string Narration = "Let me check that quickly.";
    private const string PartialAnswer = "Anna works in the gro";
    private const string MutationMessage = "Ändere die Telefonnummer von Frau Müller";
    private const string RecipeName = "create-group";
    private const string RecipeGoal = "Create a new group.";
    private const string AskPrompt = "Which group?";
    private const string ConfirmationChip = "[REPLIES:single \"Yes=yes\" | \"No=no\"]";
    private const string ConfirmationFrame = "Do you want to start this guided action: \"" + RecipeGoal + "\"?";

    private static readonly string PendingNotesHint = string.Format(
        System.Globalization.CultureInfo.InvariantCulture, PendingNotesPromptConstants.HintTemplate, 2);

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
        recovery.ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
    }

    private static void TheLastRequestWasTheNoToolsRecoveryCall(LLMServiceTurnHarness harness)
    {
        harness.Requests.Count.ShouldBe(2);
        var recovery = harness.Requests[^1];
        recovery.AvailableFunctions.ShouldBeEmpty();
        recovery.VolatileSystemPrompt!.ShouldContain(EmptyAnswerRecoveryConstants.ToolLessRecoveryInstruction);
        recovery.VolatileSystemPrompt.ShouldNotContain(EmptyAnswerRecoveryConstants.RecoveryInstruction);
        recovery.Message.ShouldBe(UserMessage);
        recovery.ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
    }

    private static LLMContext ContextWithPendingNotes(string? language = "en")
    {
        var context = LLMServiceTurnHarness.Context(UserMessage, language);
        context.EntityGroundingBlock = PendingNotesHint;
        return context;
    }

    private static async Task<string> RunAsync(LLMServiceTurnHarness harness, LLMContext context, bool streaming)
    {
        if (streaming)
        {
            return LLMServiceTurnHarness.StreamedContent(await harness.StreamAsync(context));
        }

        return (await harness.Service.ProcessAsync(context)).Message;
    }

    private static RecipeExecutionPlan ConfirmationPlan() =>
        new(RecipeName, RecipeSteps(), needsConfirmation: true, goal: RecipeGoal);

    private static RecipeExecutionPlan AskPlan() => new(RecipeName, RecipeSteps(), goal: RecipeGoal);

    private static List<RecipeStep> RecipeSteps() =>
    [
        new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "groupName", Prompt = AskPrompt },
        new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = "create_group" }
    ];

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

    [TestCase(true)]
    [TestCase(false)]
    public async Task NoToolsAndEmptyAnswer_IsRecoveredWithTheToolLessInstruction(bool streaming)
    {
        var harness = ScriptedTurn(streaming,
            LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(RecoveredAnswer));

        var answer = await RunAsync(harness, LLMServiceTurnHarness.Context(UserMessage), streaming);

        answer.ShouldBe(RecoveredAnswer);
        harness.PersistedAnswer.ShouldBe(RecoveredAnswer);
        TheLastRequestWasTheNoToolsRecoveryCall(harness);
    }

    // Before 2026-09-24 this turn ended with the tools-ran notice ("I ran the requested steps ..."), which is
    // untrue when no tool ran at all. The no-action notice says what really happened: nothing was executed.
    [TestCase(true)]
    [TestCase(false)]
    public async Task NoToolsAndEmptyAnswer_RecoveryEmptyAgain_EndsWithTheLocalizedNoActionNotice(bool streaming)
    {
        var harness = ScriptedTurn(streaming, LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(" "));
        var germanNotice = GracefulCorrectionTexts.VariantsOf(
            GracefulCorrectionTexts.EmptyAnswerNoActionNotice)["de"];

        var answer = await RunAsync(harness, LLMServiceTurnHarness.Context(UserMessage, language: "de"), streaming);

        answer.ShouldBe(germanNotice);
        harness.PersistedAnswer.ShouldBe(germanNotice);
        answer.ShouldNotBe(GracefulCorrectionTexts.VariantsOf(GracefulCorrectionTexts.EmptyAnswerFallbackNotice)["de"]);
        harness.Requests.Count.ShouldBe(2);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task NoToolsAndEmptyAnswer_RecoveryEmptyAgain_UnknownLanguageFallsBackToTheEnglishNoActionNotice(bool streaming)
    {
        var harness = ScriptedTurn(streaming, LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(" "));

        var answer = await RunAsync(harness, LLMServiceTurnHarness.Context(UserMessage, language: "xx-XX"), streaming);

        answer.ShouldBe(EmptyAnswerRecoveryConstants.NoActionNotice);
        harness.PersistedAnswer.ShouldBe(EmptyAnswerRecoveryConstants.NoActionNotice);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task NoToolsAndEmptyAnswer_RecoveryCallFails_EndsWithTheNoActionNotice(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(
            LLMServiceTurnHarness.Text(string.Empty),
            streaming
                ? harness.StreamBreaksOffAfter(string.Empty)
                : new LLMProviderResponse { Success = false, Error = "invalid api key" });

        var answer = await RunAsync(harness, LLMServiceTurnHarness.Context(UserMessage), streaming);

        answer.ShouldBe(EmptyAnswerRecoveryConstants.NoActionNotice);
        harness.Requests.Count.ShouldBe(2);
    }

    [TestCase("Done.", "en")]
    [TestCase("Erledigt.", "de")]
    [TestCase("I have created the group Bern.", "en")]
    public async Task NonStreaming_NoToolsRecoveryClaimsACompletedAction_IsReplacedByTheNoActionNotice(
        string claim, string language)
    {
        CompletionClaimDetector.ClaimsCompletion(claim).ShouldBeTrue();
        var harness = ScriptedTurn(streaming: false, LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(claim));
        var notice = GracefulCorrectionTexts.VariantsOf(GracefulCorrectionTexts.EmptyAnswerNoActionNotice)[language];

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage, language));

        response.Message.ShouldBe(notice);
        harness.PersistedAnswer.ShouldBe(notice);
        harness.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task NonStreaming_NoToolsRecoveryAsksAQuestion_IsKeptEvenThoughItMentionsACompletion()
    {
        const string question = "I have created a draft - shall I save it?";
        CompletionClaimDetector.ClaimsCompletion(question).ShouldBeTrue();
        var harness = ScriptedTurn(streaming: false, LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(question));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        response.Message.ShouldBe(question);
        harness.PersistedAnswer.ShouldBe(question);
    }

    [Test]
    public async Task NonStreaming_RecoveryAfterSuccessfulTools_KeepsItsCompletionClaim()
    {
        const string claim = "Done.";
        var harness = ScriptedTurn(streaming: false, TwoToolIterationsThenEcho(LLMServiceTurnHarness.Text(claim)));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        response.Message.ShouldBe(claim);
        TheLastRequestWasTheToolLessRecoveryCall(harness);
    }

    [Test]
    public async Task NonStreaming_NoToolsRecoveryWithAnOrdinaryAnswer_IsKept()
    {
        var harness = ScriptedTurn(streaming: false, LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(RecoveredAnswer));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        CompletionClaimDetector.ClaimsCompletion(RecoveredAnswer).ShouldBeFalse();
        response.Message.ShouldBe(RecoveredAnswer);
    }

    // The streaming path cannot withhold text that is already on screen; its correction is the no-action
    // notice TurnClosingNotices appends below the claim. Pinned so that the two paths cannot drift apart
    // silently: the non-streaming replacement above and this appended correction are the same guard.
    [Test]
    public async Task Streaming_NoToolsRecoveryClaimsACompletedAction_GetsTheNoActionCorrectionAppended()
    {
        const string claim = "Done.";
        var harness = ScriptedTurn(streaming: true, LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(claim));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(claim + MutationGuardConstants.NoActionStreamNotice);
        harness.PersistedAnswer.ShouldBe(claim + MutationGuardConstants.NoActionStreamNotice);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Recovery_DropsThePendingNotesHintThatTheLoopRequestCarried(bool streaming)
    {
        var harness = ScriptedTurn(streaming,
            LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(RecoveredAnswer));

        await RunAsync(harness, ContextWithPendingNotes(), streaming);

        harness.Requests.Count.ShouldBe(2);
        harness.Requests[0].VolatileSystemPrompt!.ShouldContain(PendingNotesPromptConstants.Marker);
        harness.Requests[1].VolatileSystemPrompt!.ShouldNotContain(PendingNotesPromptConstants.Marker);
        harness.Requests[1].VolatileSystemPrompt.ShouldNotContain(SkillNames.ManagePendingNotes);
    }

    [Test]
    public async Task NonStreaming_ForceToolNudgeAlsoEmpty_RecoversAgainstTheUserMessageNotTheNudge()
    {
        var harness = ScriptedTurn(streaming: false,
            LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(string.Empty),
            LLMServiceTurnHarness.Text(RecoveredAnswer));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(MutationMessage));

        response.Message.ShouldBe(RecoveredAnswer);
        harness.Requests.Count.ShouldBe(3);
        harness.Requests[1].Message.ShouldBe(MutationGuardConstants.ForceToolNudge);
        harness.Requests[2].Message.ShouldBe(MutationMessage);
        harness.Requests[2].VolatileSystemPrompt!.ShouldContain(EmptyAnswerRecoveryConstants.ToolLessRecoveryInstruction);
    }

    // The nudge appended the user's message and the nudged answer to the running history; the recovery
    // sends the user's message again as its own message, so without dropping that exchange the message
    // reached the model twice.
    [Test]
    public async Task NonStreaming_ForceToolNudgeAlsoEmpty_RecoveryHistoryDoesNotRepeatTheUserMessage()
    {
        var harness = ScriptedTurn(streaming: false,
            LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(string.Empty),
            LLMServiceTurnHarness.Text(RecoveredAnswer));

        await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(MutationMessage));

        harness.Requests.Count.ShouldBe(3);
        harness.Requests[1].ConversationHistory.ShouldContain(
            m => m.Role == LLMMessageRoles.User && m.Content == MutationMessage);
        harness.Requests[2].ConversationHistory.ShouldNotContain(
            m => m.Role == LLMMessageRoles.User && m.Content == MutationMessage);
        harness.Requests[2].ConversationHistory.ShouldNotContain(
            m => m.Content == LLMLoopConstants.NoActionHistoryNote);
    }

    [Test]
    public void WithoutNudgeExchange_DropsTheTrailingExchangeOnCopyAndLeavesTheRunningHistoryAlone()
    {
        var earlier = new ProviderMessage { Role = LLMMessageRoles.User, Content = "Earlier question" };
        var running = new List<ProviderMessage>
        {
            earlier,
            new() { Role = LLMMessageRoles.User, Content = MutationMessage },
            new() { Role = LLMMessageRoles.Assistant, Content = LLMLoopConstants.NoActionHistoryNote }
        };

        var trimmed = ForceToolNudgePolicy.WithoutNudgeExchange(running, MutationMessage);

        trimmed.ShouldBe(new[] { earlier });
        running.Count.ShouldBe(3);
    }

    [Test]
    public void WithoutNudgeExchange_TailIsNotTheExchange_ReturnsAnUnchangedCopy()
    {
        var running = new List<ProviderMessage>
        {
            new() { Role = LLMMessageRoles.User, Content = "Another message" },
            new() { Role = LLMMessageRoles.Assistant, Content = "Another answer" }
        };

        var copy = ForceToolNudgePolicy.WithoutNudgeExchange(running, MutationMessage);

        copy.ShouldBe(running);
        copy.ShouldNotBeSameAs(running);
        ForceToolNudgePolicy.WithoutNudgeExchange(new List<ProviderMessage>(), MutationMessage).ShouldBeEmpty();
    }

    [Test]
    public async Task Streaming_RecipeConfirmationStepAnsweredEmpty_UsesTheDeterministicFrameAndChipWithoutRecovery()
    {
        var harness = ScriptedTurn(streaming: true, LLMServiceTurnHarness.Text(string.Empty));
        harness.StartsRecipe(ConfirmationPlan());

        var answer = await RunAsync(harness, ContextWithPendingNotes(), streaming: true);

        answer.ShouldStartWith(ConfirmationFrame);
        answer.ShouldEndWith(ConfirmationChip);
        TheOnlyRequestWasTheRecipeStep(harness);
    }

    [Test]
    public async Task NonStreaming_RecipeConfirmationStepAnsweredEmpty_UsesTheDeterministicFrameAndChipWithoutRecovery()
    {
        var harness = ScriptedTurn(streaming: false, LLMServiceTurnHarness.Text(string.Empty));
        harness.StartsRecipe(ConfirmationPlan());

        var response = await harness.Service.ProcessAsync(ContextWithPendingNotes());

        response.Message.ShouldStartWith(ConfirmationFrame);
        response.SuggestedReplies.ShouldNotBeNull();
        TheOnlyRequestWasTheRecipeStep(harness);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RecipeAskStepAnsweredEmpty_UsesTheAuthoredQuestionWithoutRecovery(bool streaming)
    {
        var harness = ScriptedTurn(streaming, LLMServiceTurnHarness.Text(string.Empty));
        harness.StartsRecipe(AskPlan());

        var answer = await RunAsync(harness, ContextWithPendingNotes(), streaming);

        answer.ShouldBe(AskPrompt);
        TheOnlyRequestWasTheRecipeStep(harness);
    }

    private static void TheOnlyRequestWasTheRecipeStep(LLMServiceTurnHarness harness)
    {
        harness.Requests.Count.ShouldBe(1);
        harness.Requests[0].AvailableFunctions.ShouldBeEmpty();
        harness.Requests[0].ThinkingBudgetTokens.ShouldBe(ThinkingBudgetConstants.Disabled);
        harness.Requests[0].VolatileSystemPrompt!.ShouldNotContain(PendingNotesPromptConstants.Marker);
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

        EmptyAnswerRecovery.NeedsRecovery(string.Empty, calls, endedOnUiPassthrough: () => true, pausedOnRecipeStep: false)
            .ShouldBeFalse();
        EmptyAnswerRecovery.NeedsRecovery(string.Empty, calls, endedOnUiPassthrough: () => false, pausedOnRecipeStep: false)
            .ShouldBeTrue();
    }

    [Test]
    public void NeedsRecovery_WithoutAnyCall_IgnoresThePassthroughStateAndHonoursTheRecipePause()
    {
        var noCalls = new List<LLMFunctionCall>();
        Func<bool> mustNotBeRead = () => throw new InvalidOperationException("passthrough state read without a call");

        EmptyAnswerRecovery.NeedsRecovery(string.Empty, noCalls, mustNotBeRead, pausedOnRecipeStep: false).ShouldBeTrue();
        EmptyAnswerRecovery.NeedsRecovery(string.Empty, noCalls, mustNotBeRead, pausedOnRecipeStep: true).ShouldBeFalse();
        EmptyAnswerRecovery.NeedsRecovery(RecoveredAnswer, noCalls, mustNotBeRead, pausedOnRecipeStep: false).ShouldBeFalse();
    }

    [Test]
    public void NeedsRecovery_PausedOnRecipeStep_NeverRecoversEvenAfterSuccessfulCalls()
    {
        var calls = new List<LLMFunctionCall> { new() { FunctionName = "get_employee", Success = true } };

        EmptyAnswerRecovery.NeedsRecovery(string.Empty, calls, () => false, pausedOnRecipeStep: true).ShouldBeFalse();
    }
}
