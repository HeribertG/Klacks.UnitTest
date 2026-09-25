// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The cooperative stop of a streamed turn: a stop request is honoured at the safe points of the turn
/// (before the recipe resolve, before every model call, inside the provider stream, after a model round and
/// right before the turn commits) and ends the turn on turn_stopped followed by exactly one done - nothing
/// else, because the client keeps processing the stream after a confirmed stop. A stop that arrives after
/// the last safe point is not served, and a dropped connection is not a stop: it leaves the turn without an
/// outcome for the safety net and without an error log.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceStopTests
{
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string SkillName = "get_employee";
    private const string NeverStreamed = "This round must never happen.";
    private const int StopAfterMs = 50;
    private const int SlowCallMs = 5_000;

    [Test]
    public async Task AStopBeforeTheFirstModelRound_CallsNoProviderAndEndsOnTurnStoppedThenDone()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var context = ContextWith(stop.Token);

        var chunks = await StreamAsync(harness, context, requestToken: default);

        harness.Requests.ShouldBeEmpty();
        await harness.TurnPreparation.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default);
        Kinds(chunks).ShouldBe([SseChunkType.StreamStart, SseChunkType.TurnStopped, SseChunkType.Done]);
        chunks.Single(c => c.Type == SseChunkType.TurnStopped).TurnId.ShouldBe(context.TurnId);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopWhileTheProviderStreams_KeepsTheTextEndsCleanlyAndLogsNoError()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is, with all of her details."));
        using var stop = new CancellationTokenSource();
        var context = ContextWith(stop.Token);

        var chunks = await StreamAsync(
            harness, context, requestToken: default, onChunk: chunk => CancelOn(chunk, SseChunkType.Content, stop));

        var streamed = LLMServiceTurnHarness.StreamedContent(chunks);
        streamed.ShouldNotBeEmpty();
        "Here she is, with all of her details.".ShouldStartWith(streamed);
        streamed.ShouldNotBe("Here she is, with all of her details.");
        harness.TurnState.StreamedContent.ToString().ShouldBe(streamed);
        harness.Requests.Count.ShouldBe(1);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        harness.Logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopDuringAToolRound_StartsNoSecondModelRoundNoRecoveryAndNoMetadata()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(
            LLMServiceTurnHarness.ToolCall(SkillName), LLMServiceTurnHarness.Text(NeverStreamed));
        using var stop = new CancellationTokenSource();
        harness.SkillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stop.Cancel();
                return new SkillBridgeResult { Success = true, ResultType = "Data", Message = "Skill result." };
            });

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), requestToken: default);

        harness.Requests.Count.ShouldBe(1);
        LLMServiceTurnHarness.StreamedContent(chunks).ShouldNotContain(NeverStreamed);
        harness.TurnState.Calls.ShouldHaveSingleItem().FunctionName.ShouldBe(SkillName);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopWhileANonStreamingProviderCallIsRunning_CancelsTheCallAndEndsCleanly()
    {
        var harness = new LLMServiceTurnHarness(streaming: false);
        harness.Provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(SlowCallMs, call.Arg<CancellationToken>());
                return LLMServiceTurnHarness.Text(NeverStreamed);
            });
        using var stop = new CancellationTokenSource();
        stop.CancelAfter(StopAfterMs);

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), requestToken: default);

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBeEmpty();
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        harness.Logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopWhileTheRecipeQuestionIsBeingAsked_CancelsTheCallAndDoesNotPauseTheRecipe()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.StartsRecipe(new RecipeExecutionPlan(
            "guided-setup",
            [new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "groupName", Prompt = "Which group?" }],
            needsConfirmation: true));
        harness.Provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(SlowCallMs, call.Arg<CancellationToken>());
                return LLMServiceTurnHarness.Text(NeverStreamed);
            });
        using var stop = new CancellationTokenSource();
        stop.CancelAfter(StopAfterMs);

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), requestToken: default);

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBeEmpty();
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        AssertStoppedEnding(chunks);
    }

    [Test]
    public async Task AStopThatArrivesWhileTheTurnCommits_IsNotServedAndTheTurnEndsRegularly()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));
        using var stop = new CancellationTokenSource();
        harness.OnMessageSaved = stop.Cancel;

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), requestToken: default);

        Kinds(chunks).ShouldContain(SseChunkType.Metadata);
        Kinds(chunks).ShouldNotContain(SseChunkType.TurnStopped);
        Kinds(chunks).Last().ShouldBe(SseChunkType.Done);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Completed);
    }

    [Test]
    public async Task ADroppedConnection_IsNoStopAndLeavesNoOutcomeNoUsageRowAndNoErrorLog()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is, with all of her details."));
        using var requestAborted = new CancellationTokenSource();
        var context = ContextWith(CancellationToken.None);

        var chunks = await StreamAsync(
            harness, context, requestAborted.Token, onChunk: chunk => CancelOn(chunk, SseChunkType.Content, requestAborted));

        Kinds(chunks).ShouldNotContain(SseChunkType.TurnStopped);
        Kinds(chunks).ShouldNotContain(SseChunkType.Done);
        Kinds(chunks).ShouldNotContain(SseChunkType.Metadata);
        Kinds(chunks).ShouldNotContain(SseChunkType.Error);
        harness.TurnState.Outcome.ShouldBeNull();
        harness.TurnState.StreamedContent.Length.ShouldBeGreaterThan(0);
        harness.TrackedUsage.ShouldBeNull();
        harness.Logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task TheCommitTail_RunsWithATokenNoCancellationCanReach()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));
        using var requestAborted = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();

        await StreamAsync(harness, ContextWith(stop.Token), requestAborted.Token);

        var agentLookups = harness.AgentRepository.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentRepository.GetDefaultAgentAsync))
            .Select(call => call.GetArguments().OfType<CancellationToken>().Single())
            .ToList();
        agentLookups.Last().CanBeCanceled.ShouldBeFalse();
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

    private static List<SseChunkType> Kinds(IEnumerable<SseChunk> chunks) =>
        chunks.Where(c => c.Type != SseChunkType.Status).Select(c => c.Type).ToList();

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
