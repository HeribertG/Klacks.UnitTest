// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The closing of a streamed turn - the empty-answer recovery, the recipe re-ask and the closing notices -
/// under a stop request and under a consumer that walks away. The turn's run state is fed BEFORE each chunk
/// is offered, so what the state holds is what the client was given even when the iterator is abandoned in
/// the middle of the closing. A stop cancels the recovery call itself and ends the turn as stopped instead
/// of failing it. A stop that arrives while a recipe question is being put leaves the recipe where it was;
/// one that arrives after the recipe was already paused or re-asked does not undo that.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceStopClosingTests
{
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string SkillName = "get_employee";
    private const string RecoveredAnswer = "Anna Meier exists in the system already.";
    private const string NeverStreamed = "This round must never happen.";
    private const string ClaimingAnswer = "I created the employee Anna Meier.";
    private const string RecipeQuestion = "Which group?";
    private const string IndependentAnswer = "The import runs through a drop point.";
    private const int SlowCallMs = 5_000;

    [Test]
    public async Task TheRecoveryText_IsInTheStateAsItIsShown_WhenTheConsumerAbandonsMidRecovery()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(
            LLMServiceTurnHarness.ToolCall(SkillName),
            LLMServiceTurnHarness.Text(string.Empty),
            LLMServiceTurnHarness.Text(RecoveredAnswer));
        var received = new List<string>();

        await using (var enumerator = harness.Service.ProcessStreamAsync(ContextWith(CancellationToken.None)).GetAsyncEnumerator())
        {
            while (await enumerator.MoveNextAsync())
            {
                if (enumerator.Current.Type != SseChunkType.Content)
                {
                    continue;
                }

                received.Add(enumerator.Current.Text!);
                if (received.Count == 2)
                {
                    break;
                }
            }
        }

        received.Count.ShouldBe(2);
        harness.TurnState.StreamedContent.ToString().ShouldBe(string.Concat(received));
        harness.TurnState.Outcome.ShouldBeNull();
    }

    [Test]
    public async Task AClosingNotice_IsInTheStateWhenTheConsumerAbandonsAtIt()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text(ClaimingAnswer));
        var received = new List<string>();

        await using (var enumerator = harness.Service.ProcessStreamAsync(ContextWith(CancellationToken.None)).GetAsyncEnumerator())
        {
            while (await enumerator.MoveNextAsync())
            {
                if (enumerator.Current.Type == SseChunkType.Content)
                {
                    received.Add(enumerator.Current.Text!);
                }

                if (enumerator.Current.Type == SseChunkType.Content && string.Concat(received).Length > ClaimingAnswer.Length)
                {
                    break;
                }
            }
        }

        string.Concat(received).Length.ShouldBeGreaterThan(ClaimingAnswer.Length);
        harness.TurnState.StreamedContent.ToString().ShouldBe(string.Concat(received));
    }

    [Test]
    public async Task AStopDuringTheRecoveryStream_CancelsTheProviderCallEndsStoppedAndKeepsWhatWasShown()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(
            LLMServiceTurnHarness.ToolCall(SkillName),
            LLMServiceTurnHarness.Text(string.Empty),
            LLMServiceTurnHarness.Text(RecoveredAnswer));
        using var stop = new CancellationTokenSource();

        var chunks = await StreamAsync(
            harness, ContextWith(stop.Token), requestToken: default,
            onChunk: chunk => CancelOn(chunk, SseChunkType.Content, stop));

        var streamed = LLMServiceTurnHarness.StreamedContent(chunks);
        streamed.ShouldNotBeEmpty();
        RecoveredAnswer.ShouldStartWith(streamed);
        streamed.ShouldNotBe(RecoveredAnswer);
        harness.TurnState.StreamedContent.ToString().ShouldBe(streamed);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        harness.ProviderStreamTokens.Last().IsCancellationRequested.ShouldBeTrue();
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopThatCutsTheRecoveryStreamBeforeItsFirstToken_StreamsNoFallbackNotice()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        using var stop = new CancellationTokenSource();
        var providerCalls = 0;
        harness.Provider.ProcessStreamAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++providerCalls switch
            {
                1 => ToolCallTokens(),
                2 => EmptyTokens(),
                _ => StopThenEnd(stop)
            });

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), requestToken: default);

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBeEmpty();
        harness.TurnState.StreamedContent.Length.ShouldBe(0);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopDuringANonStreamingRecoveryCall_EndsStoppedInsteadOfFailingTheTurn()
    {
        var harness = new LLMServiceTurnHarness(streaming: false);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        harness.Provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++calls switch
            {
                1 => Task.FromResult(LLMServiceTurnHarness.ToolCall(SkillName)),
                2 => Task.FromResult(LLMServiceTurnHarness.Text(string.Empty)),
                _ => StopThenWait(stop, call.Arg<CancellationToken>())
            });

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), requestToken: default);

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldNotContain(NeverStreamed);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopAfterTheRecipeQuestionWasAnswered_StopsTheTurnButTheRecipeStaysPaused()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.StartsRecipe(AskingRecipe());
        harness.Provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(LLMServiceTurnHarness.Text(RecipeQuestion)));
        using var stop = new CancellationTokenSource();

        var chunks = await StreamAsync(
            harness, ContextWith(stop.Token), requestToken: default,
            onChunk: chunk => CancelOn(chunk, SseChunkType.Content, stop));

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldContain(RecipeQuestion);
        harness.PendingRecipes.Received(1).Save(Arg.Any<PendingRecipe>());
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopWhileTheRecipeQuestionIsBeingPut_LeavesTheRecipeUntouched()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.StartsRecipe(AskingRecipe());
        using var stop = new CancellationTokenSource();
        harness.Provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StopThenWait(stop, call.Arg<CancellationToken>()));

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), requestToken: default);

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBeEmpty();
        harness.PendingRecipes.DidNotReceiveWithAnyArgs().Save(default!);
        harness.PendingRecipes.DidNotReceiveWithAnyArgs().Clear(default, default!);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopDuringTheRecipeReask_StopsTheTurnAfterTheRecipeWasReasked()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        var plan = AskingRecipe();
        plan.MarkTopicSwitchThisTurn();
        harness.StartsRecipe(plan);
        harness.Script(LLMServiceTurnHarness.Text(IndependentAnswer));
        using var stop = new CancellationTokenSource();
        var reaskSeen = false;

        var chunks = await StreamAsync(
            harness, ContextWith(stop.Token), requestToken: default,
            onChunk: chunk =>
            {
                if (chunk.Type == SseChunkType.Content && chunk.Text!.Contains(RecipeQuestion))
                {
                    reaskSeen = true;
                    stop.Cancel();
                }
            });

        reaskSeen.ShouldBeTrue();
        harness.TurnState.StreamedContent.ToString().ShouldEndWith(RecipeQuestion);
        harness.PendingRecipes.Received(1).Save(Arg.Any<PendingRecipe>());
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        AssertStoppedEnding(chunks);
    }

    private static RecipeExecutionPlan AskingRecipe() => new(
        "guided-setup",
        [new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "groupName", Prompt = RecipeQuestion }],
        needsConfirmation: false);

    private static async IAsyncEnumerable<string> ToolCallTokens()
    {
        await Task.Yield();
        yield return LLMStreamingTokens.ToolCallPrefix
            + System.Text.Json.JsonSerializer.Serialize(new { index = 0, name = SkillName, arguments = "{}" });
        yield return LLMStreamingTokens.ToolCallEnd;
    }

    private static async IAsyncEnumerable<string> EmptyTokens()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<string> StopThenEnd(CancellationTokenSource stop)
    {
        await Task.Yield();
        stop.Cancel();
        yield break;
    }

    private static async Task<LLMProviderResponse> StopThenWait(CancellationTokenSource stop, CancellationToken token)
    {
        stop.Cancel();
        await Task.Delay(SlowCallMs, token);
        return LLMServiceTurnHarness.Text(NeverStreamed);
    }

    private static LLMContext ContextWith(CancellationToken stopToken)
    {
        var context = LLMServiceTurnHarness.Context(UserMessage);
        context.StopToken = stopToken;
        return context;
    }

    private static void CancelOn(SseChunk chunk, SseChunkType type, CancellationTokenSource source)
    {
        if (chunk.Type == type && !source.IsCancellationRequested)
        {
            source.Cancel();
        }
    }

    private static async Task<List<SseChunk>> StreamAsync(
        LLMServiceTurnHarness harness,
        LLMContext context,
        CancellationToken requestToken,
        Action<SseChunk>? onChunk = null)
    {
        var chunks = new List<SseChunk>();
        await foreach (var chunk in harness.Service.ProcessStreamAsync(context, requestToken))
        {
            chunks.Add(chunk);
            onChunk?.Invoke(chunk);
        }

        return chunks;
    }

    private static void AssertStoppedEnding(IReadOnlyList<SseChunk> chunks)
    {
        var stoppedAt = chunks.ToList().FindIndex(c => c.Type == SseChunkType.TurnStopped);
        stoppedAt.ShouldBeGreaterThanOrEqualTo(0);
        chunks.Count(c => c.Type == SseChunkType.TurnStopped).ShouldBe(1);
        chunks.Skip(stoppedAt + 1).Select(c => c.Type).ShouldBe([SseChunkType.Done]);
        chunks.ShouldNotContain(c => c.Type == SseChunkType.Metadata);
        chunks.ShouldNotContain(c => c.Type == SseChunkType.Error);
    }
}
