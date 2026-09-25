// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A turn the user stops is still a turn of the conversation: before the client hears turn_stopped, its
/// history holds the user's message and the partial answer with the interruption marker, its usage row exists,
/// the correction anchor knows the write that already ran, the confirmations it issued are dropped and the
/// background tasks that stay allowed have been started - while the ones that would learn from a cut-off
/// answer have not. A stop that arrives after the turn committed is not served, so nothing is cleaned up.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceStoppedTurnPersistenceTests
{
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string WriteSkill = "create_employee";
    private const string FullAnswer = "Here she is, with all of her details.";

    [Test]
    public async Task AStopWhileTheProviderStreams_PersistsThePartialAnswerFollowedByTheMarker()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text(FullAnswer));
        using var stop = new CancellationTokenSource();

        var chunks = await StreamAsync(harness, ContextWith(stop.Token), onChunk: chunk => CancelOnContent(chunk, stop));

        var streamed = LLMServiceTurnHarness.StreamedContent(chunks);
        harness.PersistedAnswer.ShouldBe(streamed + "\n" + TurnInterruptionDefaults.InterruptedMarker);
        harness.TrackedUsage.ShouldNotBeNull();
        harness.TrackedUsage!.FunctionsCalled.ShouldBe("[]");
        harness.BackgroundTasks.Received(1).RunStoppedTurnTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(),
            harness.PersistedAnswer!, Arg.Any<List<LLMFunctionCall>>(), InterruptedTurnPhases.DuringText);
        harness.BackgroundTasks.DidNotReceiveWithAnyArgs().RunBackgroundTasks(
            default, default!, default!, default!, default!, default);
        await harness.StoppedTurnCleanup.Received(1).CleanUpAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheStoppedEvent_IsSentOnlyAfterTheTurnIsPersisted()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text(FullAnswer));
        using var stop = new CancellationTokenSource();
        var chunks = new List<SseChunk>();
        var stoppedEventSeenWhenPersisting = false;
        harness.OnMessageSaved = () => stoppedEventSeenWhenPersisting |= chunks.Any(c => c.Type == SseChunkType.TurnStopped);

        await foreach (var chunk in harness.Service.ProcessStreamAsync(ContextWith(stop.Token)))
        {
            chunks.Add(chunk);
            CancelOnContent(chunk, stop);
        }

        harness.PersistedAnswer.ShouldNotBeNull();
        stoppedEventSeenWhenPersisting.ShouldBeFalse();
        chunks.Count(c => c.Type == SseChunkType.TurnStopped).ShouldBe(1);
    }

    [Test]
    public async Task AWriteThatRanBeforeTheStop_IsInTheAnchorTheUsageRowAndTheEvent()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text("This round must never happen."));
        using var stop = new CancellationTokenSource();
        harness.SkillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stop.Cancel();
                return new SkillBridgeResult { Success = true, ResultType = "Data", Message = "Employee created." };
            });

        var chunks = await StreamAsync(harness, ContextWith(stop.Token));

        var stopped = chunks.Single(c => c.Type == SseChunkType.TurnStopped);
        stopped.ExecutedCount.ShouldBe(1);
        harness.TrackedUsage!.FunctionsCalled.ShouldBe("[\"create_employee\"]");
        harness.TurnPreparation.Received(1).RecordLastAction(
            Arg.Any<LLMContext>(), LLMServiceTurnHarness.ConversationId, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<LLMFunctionCall>>(calls => calls.Select(c => c.FunctionName).SequenceEqual(new[] { WriteSkill })),
            false);
        harness.BackgroundTasks.Received(1).RunStoppedTurnTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(), Arg.Any<string>(),
            Arg.Is<List<LLMFunctionCall>>(calls => calls.Count == 1 && calls[0].FunctionName == WriteSkill),
            InterruptedTurnPhases.DuringTools);
    }

    [Test]
    public async Task AStopBeforeTheFirstModelRound_PersistsTheUserMessageAndTheMarkerAlone()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var chunks = await StreamAsync(harness, ContextWith(stop.Token));

        harness.PersistedAnswer.ShouldBe(TurnInterruptionDefaults.InterruptedMarker);
        harness.TrackedUsage.ShouldNotBeNull();
        harness.BackgroundTasks.Received(1).RunStoppedTurnTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(),
            TurnInterruptionDefaults.InterruptedMarker, Arg.Any<List<LLMFunctionCall>>(), InterruptedTurnPhases.BeforeText);
        chunks.Count(c => c.Type == SseChunkType.TurnStopped).ShouldBe(1);
    }

    [Test]
    public async Task AStopThatArrivesAfterTheTurnCommitted_LeavesTheConfirmationsAndTheUiActionRowsAlone()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));
        using var stop = new CancellationTokenSource();
        harness.OnMessageSaved = stop.Cancel;

        var chunks = await StreamAsync(harness, ContextWith(stop.Token));

        chunks.ShouldContain(c => c.Type == SseChunkType.Metadata);
        await harness.StoppedTurnCleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
        harness.BackgroundTasks.DidNotReceiveWithAnyArgs().RunStoppedTurnTasks(
            default, default!, default!, default!, default!, default!);
        harness.BackgroundTasks.Received(1).RunBackgroundTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(), Arg.Any<string>(),
            Arg.Any<List<LLMFunctionCall>>(), Arg.Any<bool>());
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Completed);
    }

    [Test]
    public async Task ATurnThatRanToItsEnd_IsNeverPersistedAsStopped()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));

        await StreamAsync(harness, ContextWith(CancellationToken.None));

        harness.PersistedAnswer.ShouldNotContain(TurnInterruptionDefaults.InterruptedMarker);
        harness.BackgroundTasks.DidNotReceiveWithAnyArgs().RunStoppedTurnTasks(
            default, default!, default!, default!, default!, default!);
    }

    private static LLMContext ContextWith(CancellationToken stopToken)
    {
        var context = LLMServiceTurnHarness.Context(UserMessage);
        context.StopToken = stopToken;
        return context;
    }

    private static void CancelOnContent(SseChunk chunk, CancellationTokenSource source)
    {
        if (chunk.Type == SseChunkType.Content && !source.IsCancellationRequested)
        {
            source.Cancel();
        }
    }

    private static async Task<List<SseChunk>> StreamAsync(
        LLMServiceTurnHarness harness, LLMContext context, Action<SseChunk>? onChunk = null)
    {
        var chunks = new List<SseChunk>();
        await foreach (var chunk in harness.Service.ProcessStreamAsync(context))
        {
            chunks.Add(chunk);
            onChunk?.Invoke(chunk);
        }

        return chunks;
    }
}
