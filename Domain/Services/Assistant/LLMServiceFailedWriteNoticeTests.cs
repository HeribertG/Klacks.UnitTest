// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Whole-turn guard for the failed-write turn that still sounds like a success: the model calls a side-effecting
/// skill, the skill fails, and the answer says "Erledigt". Both chat paths append the localized nothing-stored
/// notice and the skill's failure message whatever the prose claims, except when the answer itself denies having
/// done anything, when the call was only held for a confirmation, or when another call of the turn succeeded. The response flag
/// ActionPerformed reports only turns in which at least one call succeeded.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceFailedWriteNoticeTests
{
    private const string WriteSkill = "update_employee";
    private const string UserMessage = "Ändere den Mitarbeiter.";
    private const string FalseSuccess = "Erledigt – ich habe den Mitarbeiter angepasst.";
    private const string HonestDenial = "Nein, ich habe nichts gespeichert, das hat nicht geklappt.";
    private const string Language = "de";

    private static LLMContext ContextWithWriteSkill()
    {
        var context = LLMServiceTurnHarness.Context(UserMessage, Language);
        context.AvailableFunctions.Add(new LLMFunction { Name = WriteSkill });
        return context;
    }

    private const string SkillFailureResult = "Error: Skill failed.";

    private static string NothingStoredNotice()
    {
        GracefulCorrectionTexts.TryGetText(GracefulCorrectionTexts.RecipeNothingStoredNotice, Language, out var text)
            .ShouldBeTrue();
        return RecipeEngineDefaults.NothingStoredNoticeSeparator + text
            + RecipeEngineDefaults.NothingStoredFailureReasonSeparator + SkillFailureResult;
    }

    private static async Task<LLMResponse?> RunTurnAsync(LLMServiceTurnHarness harness, bool streaming)
    {
        var context = ContextWithWriteSkill();
        if (!streaming)
        {
            return await harness.Service.ProcessAsync(context);
        }

        await harness.StreamAsync(context);
        return null;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedWriteFollowedBySuccessProse_AppendsTheNothingStoredNotice(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.SkillsFail();
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(FalseSuccess));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(FalseSuccess + NothingStoredNotice());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedWriteFollowedByAnHonestDenial_AppendsNothing(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.SkillsFail();
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(HonestDenial));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(HonestDenial);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SuccessfulWrite_AppendsNothing(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.SkillsSucceed();
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(FalseSuccess));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(FalseSuccess);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WriteHeldForConfirmation_AppendsNothing(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.SkillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult
            {
                Success = false,
                ResultType = nameof(SkillResultType.Confirmation),
                Message = "Please confirm."
            });
        const string askForConfirmation = "Soll ich den Mitarbeiter wirklich anpassen?";
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(askForConfirmation));

        await RunTurnAsync(harness, streaming);

        harness.PersistedAnswer.ShouldBe(askForConfirmation);
    }

    [Test]
    public async Task StreamedFailedWrite_SendsTheNoticeAsContent()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.SkillsFail();
        harness.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(FalseSuccess));

        var chunks = await harness.StreamAsync(ContextWithWriteSkill());

        LLMServiceTurnHarness.StreamedContent(chunks).ShouldBe(FalseSuccess + NothingStoredNotice());
    }

    [Test]
    public async Task FailedWrite_ReportsNoActionPerformed_OnBothPaths()
    {
        var nonStreaming = new LLMServiceTurnHarness(streaming: false);
        nonStreaming.SkillsFail();
        nonStreaming.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(FalseSuccess));
        var streaming = new LLMServiceTurnHarness(streaming: true);
        streaming.SkillsFail();
        streaming.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(FalseSuccess));

        var response = await RunTurnAsync(nonStreaming, streaming: false);
        var chunks = await streaming.StreamAsync(ContextWithWriteSkill());

        response!.ActionPerformed.ShouldBeFalse();
        chunks.Single(c => c.Type == SseChunkType.Metadata).ActionPerformed.ShouldBeFalse();
    }

    [Test]
    public async Task SuccessfulWrite_ReportsActionPerformed_OnBothPaths()
    {
        var nonStreaming = new LLMServiceTurnHarness(streaming: false);
        nonStreaming.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(FalseSuccess));
        var streaming = new LLMServiceTurnHarness(streaming: true);
        streaming.Script(LLMServiceTurnHarness.ToolCall(WriteSkill), LLMServiceTurnHarness.Text(FalseSuccess));

        var response = await RunTurnAsync(nonStreaming, streaming: false);
        var chunks = await streaming.StreamAsync(ContextWithWriteSkill());

        response!.ActionPerformed.ShouldBeTrue();
        chunks.Single(c => c.Type == SseChunkType.Metadata).ActionPerformed.ShouldBeTrue();
    }
}
