// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A turn whose provider fails after the server already ran a write action keeps that action, so once the client
/// has its error chunk the turn is persisted the way a stopped one is - history under the neutral error marker,
/// usage, correction anchor, cleanup - by the interrupted-turn safety net, exactly once, and never as a stop.
/// A provider failure before any write, or after nothing but lookups, stays what it always was: nothing stored.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceErroredTurnPersistenceTests
{
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string WriteSkill = "create_employee";
    private const string ReadSkill = "get_employee";
    private const string PartialAnswer = "Anna Meier is created, and her de";

    [Test]
    public async Task AProviderFailureAfterAWrite_KeepsTheErrorChunkAndPersistsTheTurnUnderTheErrorMarker()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), harness.StreamBreaksOffAfter(PartialAnswer));
        var finalizer = FinalizerOf(harness);
        var context = LLMServiceTurnHarness.Context(UserMessage);

        var chunks = await harness.StreamAsync(context);

        chunks.ShouldContain(c => c.Type == SseChunkType.Error);
        chunks.ShouldNotContain(c => c.Type == SseChunkType.TurnStopped);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
        harness.PersistedAnswer.ShouldBeNull();

        await finalizer.FinalizeAsync(context.UserId, context.TurnId!.Value, endedInError: false);

        harness.PersistedAnswer.ShouldNotBeNull();
        harness.PersistedAnswer!.ShouldEndWith("\n" + TurnInterruptionDefaults.ErroredMarker);
        harness.PersistedAnswer.ShouldNotContain(TurnInterruptionDefaults.InterruptedMarker);
        harness.TrackedUsage.ShouldNotBeNull();
        harness.TrackedUsage!.HasError.ShouldBeTrue();
        harness.TrackedUsage.FunctionsCalled.ShouldBe("[\"create_employee\"]");
        harness.TurnPreparation.Received(1).RecordLastAction(
            Arg.Any<LLMContext>(), LLMServiceTurnHarness.ConversationId, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<LLMFunctionCall>>(calls => calls.Select(c => c.FunctionName).SequenceEqual(new[] { WriteSkill })),
            false);
        await harness.StoppedTurnCleanup.Received(1).CleanUpAsync(
            context.UserId, context.TurnId.Value, Arg.Any<CancellationToken>());
        harness.BackgroundTasks.Received(1).RunStoppedTurnTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(), Arg.Any<string>(),
            Arg.Is<List<LLMFunctionCall>>(calls => calls.Count == 1 && calls[0].FunctionName == WriteSkill),
            InterruptedTurnPhases.DuringText);
        harness.BackgroundTasks.DidNotReceiveWithAnyArgs().RunBackgroundTasks(
            default, default!, default!, default!, default!, default);
    }

    [Test]
    public async Task AProviderFailureAfterAWrite_IsPersistedOnceHoweverOftenTheNetIsReached()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), harness.StreamBreaksOffAfter(PartialAnswer));
        var finalizer = FinalizerOf(harness);
        var context = LLMServiceTurnHarness.Context(UserMessage);

        await harness.StreamAsync(context);
        await finalizer.FinalizeAsync(context.UserId, context.TurnId!.Value, endedInError: false);
        await finalizer.FinalizeAsync(context.UserId, context.TurnId.Value, endedInError: true);

        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
        await harness.StoppedTurnCleanup.Received(1).CleanUpAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        harness.BackgroundTasks.Received(1).RunStoppedTurnTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(), Arg.Any<string>(),
            Arg.Any<List<LLMFunctionCall>>(), Arg.Any<string>());
    }

    [Test]
    public async Task AProviderFailureBeforeAnyCall_StoresNothing()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(harness.StreamBreaksOffAfter(PartialAnswer));
        var finalizer = FinalizerOf(harness);
        var context = LLMServiceTurnHarness.Context(UserMessage);

        await harness.StreamAsync(context);
        await finalizer.FinalizeAsync(context.UserId, context.TurnId!.Value, endedInError: false);

        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
        harness.PersistedAnswer.ShouldBeNull();
        harness.TrackedUsage.ShouldBeNull();
        await harness.StoppedTurnCleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
    }

    [Test]
    public async Task AProviderFailureAfterOnlyALookup_StoresNothing()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.ToolCall(ReadSkill), harness.StreamBreaksOffAfter(PartialAnswer));
        var finalizer = FinalizerOf(harness);
        var context = LLMServiceTurnHarness.Context(UserMessage);

        await harness.StreamAsync(context);
        await finalizer.FinalizeAsync(context.UserId, context.TurnId!.Value, endedInError: false);

        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
        harness.PersistedAnswer.ShouldBeNull();
        harness.TrackedUsage.ShouldBeNull();
        await harness.StoppedTurnCleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
    }

    private static InterruptedTurnFinalizer FinalizerOf(LLMServiceTurnHarness harness) =>
        new(harness.TurnState, harness.Recorder, harness.StoppedTurnCleanup,
            Substitute.For<ILogger<InterruptedTurnFinalizer>>());
}
