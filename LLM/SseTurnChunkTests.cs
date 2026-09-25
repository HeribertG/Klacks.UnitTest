// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the wire contract the chat client relies on to stop a turn: stream_start carries the turn id the
/// client sends to the cancel endpoint, and the turn_stopped event reports what really ran under the exact
/// field names and event name the client reads. The client ignores unknown fields but not renamed ones.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class SseTurnChunkTests
{
    private const string ConversationId = "conv-1";
    private const string TurnStoppedEventName = "turn_stopped";
    private const string ExecutedLabel = "Mitarbeiter anlegen";

    private static readonly Guid TurnId = Guid.Parse("3f2b6c1e-8d4a-4c55-9a0e-7b1d2e3f4a5b");

    private static JsonElement Serialize(SseChunk chunk) =>
        JsonDocument.Parse(JsonSerializer.Serialize(chunk, SseChunkJson.Options)).RootElement;

    [Test]
    public void StreamStart_WithATurnId_SerializesItAsAStringNextToTheConversationId()
    {
        var json = Serialize(SseChunk.StreamStart(ConversationId, TurnId));

        json.GetProperty("conversationId").GetString().ShouldBe(ConversationId);
        json.GetProperty("turnId").ValueKind.ShouldBe(JsonValueKind.String);
        json.GetProperty("turnId").GetString().ShouldBe(TurnId.ToString());
    }

    [Test]
    public void StreamStart_WithoutATurnId_OmitsTheField()
    {
        var json = Serialize(SseChunk.StreamStart(ConversationId, null));

        json.TryGetProperty("turnId", out _).ShouldBeFalse();
    }

    [Test]
    public void StreamStart_WithTheOneArgumentOverload_StillOmitsTheField()
    {
        var chunk = SseChunk.StreamStart(ConversationId);

        chunk.TurnId.ShouldBeNull();
        chunk.ConversationId.ShouldBe(ConversationId);
        chunk.Type.ShouldBe(SseChunkType.StreamStart);
    }

    [Test]
    public void TurnStopped_CarriesTurnIdLabelsAndCount()
    {
        var chunk = SseChunk.TurnStopped(TurnId, [ExecutedLabel], 1);

        chunk.Type.ShouldBe(SseChunkType.TurnStopped);
        var json = Serialize(chunk);
        json.GetProperty("turnId").GetString().ShouldBe(TurnId.ToString());
        json.GetProperty("executedSkillLabels").EnumerateArray().Select(e => e.GetString())
            .ShouldBe([ExecutedLabel]);
        json.GetProperty("executedCount").GetInt32().ShouldBe(1);
    }

    [Test]
    public void TurnStopped_WithNothingExecuted_KeepsTheEmptyListAndTheZeroOnTheWire()
    {
        var json = Serialize(SseChunk.TurnStopped(TurnId, [], 0));

        json.GetProperty("executedSkillLabels").GetArrayLength().ShouldBe(0);
        json.GetProperty("executedCount").GetInt32().ShouldBe(0);
    }

    [Test]
    public void TurnStopped_WithMoreExecutedThanLabels_ReportsBothNumbersUnchanged()
    {
        var json = Serialize(SseChunk.TurnStopped(TurnId, [ExecutedLabel], 3));

        json.GetProperty("executedSkillLabels").GetArrayLength().ShouldBe(1);
        json.GetProperty("executedCount").GetInt32().ShouldBe(3);
    }

    [Test]
    public void TurnStopped_HasTheWireEventName()
    {
        SseEventNames.TurnStopped.ShouldBe(TurnStoppedEventName);
        SseEventNames.For(SseChunkType.TurnStopped).ShouldBe(TurnStoppedEventName);
    }

    [Test]
    public void TurnStopped_IsAppendedAfterEveryExistingChunkType()
    {
        Enum.GetValues<SseChunkType>().Max().ShouldBe(SseChunkType.TurnStopped);
    }
}
