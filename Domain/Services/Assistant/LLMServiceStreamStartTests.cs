// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The streamed turn announces its turn id on stream_start, the one event the client reads it from. The id
/// is the one the controller registered for the stop request, so it must be the context's own TurnId and
/// not a fresh one; a context without one announces none.
/// </summary>

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceStreamStartTests
{
    private const string UserMessage = "Show me the employee.";

    [Test]
    public async Task StreamStart_CarriesTheContextsTurnId()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));
        var context = LLMServiceTurnHarness.Context(UserMessage);

        var chunks = await harness.StreamAsync(context);

        var start = chunks.Single(c => c.Type == SseChunkType.StreamStart);
        start.TurnId.ShouldBe(context.TurnId);
        start.TurnId.ShouldNotBeNull();
    }

    [Test]
    public async Task StreamStart_WithoutAContextTurnId_AnnouncesNone()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));
        var context = LLMServiceTurnHarness.Context(UserMessage);
        context.TurnId = null;

        var chunks = await harness.StreamAsync(context);

        chunks.Single(c => c.Type == SseChunkType.StreamStart).TurnId.ShouldBeNull();
    }
}
