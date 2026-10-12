// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Whole-turn check of the toolset membership guard on both chat paths, in the mode the service ships with
/// (Observe): a call to a skill that is not in the turn's toolset still reaches the skill layer and keeps its real
/// outcome, but it is logged as a warning and flagged on the call, and the executor logs its outcome (Success,
/// result kind, turn ids) so the rate of outside calls that succeeded is countable; a call to an offered skill, an
/// alias of an offered skill and confirm_pending_action are neither logged nor flagged.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceToolsetMembershipTests
{
    private const string OutsideSkill = "delete_client";
    private const string UserMessage = "Show me the employee.";
    private const string Answer = "Anna works in Bern.";

    private static async Task RunTurnAsync(LLMServiceTurnHarness harness, bool streaming)
    {
        var context = LLMServiceTurnHarness.Context(UserMessage);
        if (streaming)
        {
            await harness.StreamAsync(context);
            return;
        }

        await harness.Service.ProcessAsync(context);
    }

    private static void SkillBridgeReceived(LLMServiceTurnHarness harness, string skillName) =>
        harness.SkillBridge.Received(1).ExecuteSkillFromLLMCallAsync(
            Arg.Is<LLMFunctionCall>(call => call.FunctionName == skillName),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallOutsideTheToolset_InObserveMode_StillRunsAndIsLogged(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.ToolCall(OutsideSkill), LLMServiceTurnHarness.Text(Answer));

        await RunTurnAsync(harness, streaming);

        SkillBridgeReceived(harness, OutsideSkill);
        harness.Logger.Entries.ShouldContain(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(OutsideSkill));
        harness.PersistedAnswer.ShouldBe(Answer);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallOutsideTheToolset_InObserveMode_HasItsOutcomeLogged_WhichProvesTheFlagOnTheCall(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.ToolCall(OutsideSkill), LLMServiceTurnHarness.Text(Answer));

        await RunTurnAsync(harness, streaming);

        var outcome = harness.ExecutorLogger.Entries.Single(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Outside-toolset call"));
        outcome.Message.ShouldContain(OutsideSkill);
        outcome.Message.ShouldContain("Success True");
        outcome.Message.ShouldContain(LLMServiceTurnHarness.ConversationId);
    }

    [Test]
    public async Task CallOutsideTheToolset_ThatFails_HasItsFailureOutcomeLogged_AndIsNotRejected()
    {
        var harness = new LLMServiceTurnHarness(streaming: false);
        harness.SkillsFail();
        harness.Script(LLMServiceTurnHarness.ToolCall(OutsideSkill), LLMServiceTurnHarness.Text(Answer));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(UserMessage));

        response!.ActionPerformed.ShouldBeFalse();
        SkillBridgeReceived(harness, OutsideSkill);
        harness.ExecutorLogger.Entries.ShouldContain(
            entry => entry.Message.Contains("Outside-toolset call") && entry.Message.Contains("Success False"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallToAnOfferedSkill_HasNoOutcomeLogged(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.ToolCall("get_employee"), LLMServiceTurnHarness.Text(Answer));

        await RunTurnAsync(harness, streaming);

        harness.ExecutorLogger.Entries.ShouldNotContain(entry => entry.Message.Contains("Outside-toolset call"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallToAnOfferedSkill_IsNotLogged(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.ToolCall("get_employee"), LLMServiceTurnHarness.Text(Answer));

        await RunTurnAsync(harness, streaming);

        SkillBridgeReceived(harness, "get_employee");
        harness.Logger.Entries.ShouldNotContain(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("toolset"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConfirmPendingAction_IsNotLogged_EvenWhenTheToolsetLacksIt(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(
            LLMServiceTurnHarness.ToolCall(AutonomyDefaults.ConfirmPendingActionSkillName),
            LLMServiceTurnHarness.Text(Answer));

        await RunTurnAsync(harness, streaming);

        harness.Logger.Entries.ShouldNotContain(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("toolset"));
    }
}
