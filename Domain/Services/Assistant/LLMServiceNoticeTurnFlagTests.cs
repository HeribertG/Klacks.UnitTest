// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Both chat paths tell the post-turn hooks whether the stored answer is an empty-answer notice: a turn
/// that ends in the no-action notice (no tool ran) or in the fallback notice (tools ran) hands the hooks
/// answeredWithNotice = true, so memory, learning and grounding skip it; an ordinary answer and a
/// successfully recovered answer hand them false.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceNoticeTurnFlagTests
{
    private const string UserMessage = "Show me the employee and the groups.";
    private const string OrdinaryAnswer = "Anna works in the groups Bern and Basel.";

    [TestCase(false)]
    [TestCase(true)]
    public async Task TurnWithoutToolsEndingInTheNoActionNotice_FlagsTheHooks(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(string.Empty));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(EmptyAnswerRecoveryConstants.NoActionNotice);
        HooksReceived(harness, answeredWithNotice: true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TurnWithToolsEndingInTheFallbackNotice_FlagsTheHooks(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(
            LLMServiceTurnHarness.ToolCall("get_employee"),
            LLMServiceTurnHarness.Text(string.Empty),
            LLMServiceTurnHarness.Text(string.Empty));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(EmptyAnswerRecoveryConstants.FallbackNotice);
        HooksReceived(harness, answeredWithNotice: true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OrdinaryAnswer_DoesNotFlagTheHooks(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.Text(OrdinaryAnswer));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(OrdinaryAnswer);
        HooksReceived(harness, answeredWithNotice: false);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SuccessfullyRecoveredAnswer_DoesNotFlagTheHooks(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.Text(string.Empty), LLMServiceTurnHarness.Text(OrdinaryAnswer));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(OrdinaryAnswer);
        HooksReceived(harness, answeredWithNotice: false);
    }

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

    private static void HooksReceived(LLMServiceTurnHarness harness, bool answeredWithNotice) =>
        harness.BackgroundTasks.Received(1).RunBackgroundTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(), Arg.Any<string>(),
            Arg.Any<List<LLMFunctionCall>>(), Arg.Is(answeredWithNotice));
}
