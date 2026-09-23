// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Loop-level regression for the pending-notes bug: the prompt and the read result tell the model to call
/// manage_pending_notes twice in one turn - first with action "read", then with "mark_delivered" - and the
/// repeat guard rejected the second call because the skill name carries no read-only prefix. No note was
/// ever marked delivered, so every turn re-delivered all of them. Both chat paths must execute both calls,
/// and a second mark_delivered in the same turn must still be rejected.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServicePendingNotesLoopTests
{
    private const string UserMessage = "Hi, anything new for me?";
    private const string FinalAnswer = "You have one note: the team meeting moved to Friday.";
    private const string NoteId = "5b1d2f0e-3c4a-4d5e-8f90-123456789abc";

    private static LLMProviderResponse Read() => LLMServiceTurnHarness.ToolCall(
        SkillNames.ManagePendingNotes,
        new Dictionary<string, object> { [ReadOnlySkillActions.ActionParameter] = ReadOnlySkillActions.PendingNotesRead });

    private static LLMProviderResponse MarkDelivered() => LLMServiceTurnHarness.ToolCall(
        SkillNames.ManagePendingNotes,
        new Dictionary<string, object>
        {
            [ReadOnlySkillActions.ActionParameter] = "mark_delivered",
            ["noteIds"] = NoteId
        });

    private static void BothCallsReachedTheSkill(LLMServiceTurnHarness harness) =>
        harness.SkillBridge.Received(2).ExecuteSkillFromLLMCallAsync(
            Arg.Is<LLMFunctionCall>(call => call.FunctionName == SkillNames.ManagePendingNotes),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadThenMarkDelivered_BothExecute(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(Read(), MarkDelivered(), LLMServiceTurnHarness.Text(FinalAnswer));

        if (streaming)
        {
            await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));
        }
        else
        {
            await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));
        }

        BothCallsReachedTheSkill(harness);
        harness.Requests[2].Message.ShouldNotContain(LLMLoopConstants.RepeatedWriteCallRejectedResult);
        harness.PersistedAnswer.ShouldBe(FinalAnswer);
    }

    [Test]
    public async Task SecondMarkDelivered_InTheSameTurn_IsStillRejected()
    {
        var harness = new LLMServiceTurnHarness(streaming: false);
        harness.Script(Read(), MarkDelivered(), MarkDelivered(), LLMServiceTurnHarness.Text(FinalAnswer));

        await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        BothCallsReachedTheSkill(harness);
        harness.Requests[3].Message.ShouldContain(LLMLoopConstants.RepeatedWriteCallRejectedResult);
    }
}
