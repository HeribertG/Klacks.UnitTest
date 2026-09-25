// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Every way a streamed turn can end claims exactly one outcome on the turn's run state, because the
/// interrupted-turn safety net decides from it whether the turn still needs to be persisted: a turn that
/// persisted itself (completed, clarified) must never be stored a second time, and a turn that ended on an
/// error must never be labelled as interrupted by the user.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceTurnOutcomeTests
{
    private const string UserMessage = "Show me the employee.";
    private const string Clarification = "Did you mean adding clients to a group, or listing them?";
    private const string UnknownModelId = "no-such-model";

    [Test]
    public async Task ARegularTurn_ClaimsCompleted()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));

        await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Completed);
    }

    [Test]
    public async Task ACorrectionClarification_ClaimsClarified()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        var context = LLMServiceTurnHarness.Context(UserMessage);
        context.CorrectionClarificationReply = Clarification;

        await harness.StreamAsync(context);

        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Clarified);
    }

    [Test]
    public async Task AStreamThatBreaksOff_ClaimsErroredAndPersistsNothing()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(harness.StreamBreaksOffAfter("Here is a par"));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        chunks.ShouldContain(c => c.Type == SseChunkType.Error);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
        harness.PersistedAnswer.ShouldBeNull();
    }

    [Test]
    public async Task AFailedNonStreamingProviderCall_ClaimsErrored()
    {
        var harness = new LLMServiceTurnHarness(streaming: false);
        harness.Script(new LLMProviderResponse { Success = false, Error = "provider down" });

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        chunks.ShouldContain(c => c.Type == SseChunkType.Error);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
    }

    [Test]
    public async Task ANotAvailableModel_ClaimsErrored()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        var context = LLMServiceTurnHarness.Context(UserMessage);
        context.ModelId = UnknownModelId;

        var chunks = await harness.StreamAsync(context);

        chunks.ShouldContain(c => c.Type == SseChunkType.Error);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
    }

    [Test]
    public async Task AFailedContextPreparation_ClaimsErrored()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.ConversationStoreFails();

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        chunks.ShouldContain(c => c.Type == SseChunkType.Error);
        harness.TurnState.Outcome.ShouldBe(TurnOutcome.Errored);
    }

    [Test]
    public async Task TheRunState_RecordsTheTurnItRanFor()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));
        var context = LLMServiceTurnHarness.Context(UserMessage);

        await harness.StreamAsync(context);

        harness.TurnState.Context.ShouldBeSameAs(context);
        harness.TurnState.Conversation.ShouldNotBeNull();
        harness.TurnState.StreamedContent.ToString().ShouldBe("Here she is.");
    }
}
