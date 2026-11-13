// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Whole-turn check of the toolset membership guard on both chat paths, in the mode the service ships with
/// (Enforce since 2026-11-13): a call to a skill that is not in the turn's toolset never reaches the skill layer, is
/// logged as a warning and the turn still answers, with the nothing-stored notice because the rejected call was a
/// write attempt; a call to an offered skill and confirm_pending_action are neither
/// logged nor rejected. The Observe behaviour is covered by ToolsetMembershipGuardTests.
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
    public async Task CallOutsideTheToolset_IsRejected_NeverReachesTheSkill_AndIsLogged(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.ToolCall(OutsideSkill), LLMServiceTurnHarness.Text(Answer));

        await RunTurnAsync(harness, streaming);

        await harness.SkillBridge.DidNotReceive().ExecuteSkillFromLLMCallAsync(
            Arg.Is<LLMFunctionCall>(call => call.FunctionName == OutsideSkill),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());
        harness.Logger.Entries.ShouldContain(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(OutsideSkill));
        harness.PersistedAnswer.ShouldStartWith(Answer);
        harness.PersistedAnswer.ShouldContain(RecipeEngineDefaults.NothingStoredNotice);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallOutsideTheToolset_HasNoExecutorOutcome_BecauseItNeverRan(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.ToolCall(OutsideSkill), LLMServiceTurnHarness.Text(Answer));

        await RunTurnAsync(harness, streaming);

        harness.ExecutorLogger.Entries.ShouldNotContain(entry => entry.Message.Contains("Outside-toolset call"));
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
