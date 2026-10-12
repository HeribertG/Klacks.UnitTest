// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the every-write-failed notice of TurnClosingNotices: a turn that attempted side-effecting calls, none of
/// which succeeded, has stored nothing whatever the answer claims. It is owed after a failure with success prose
/// (also when that prose ends in a question), and not after an honest denial, a turn that only holds a
/// confirmation, a partial success, a blank answer (the step-failed notice's case), a call skipped by the stop or a
/// turn that only previewed. A write rejected without running (repeat, outside the toolset, after a completed
/// recipe) is an attempt that stored nothing and counts as failed; a held confirmation is judged per call, so a
/// failed write next to a held one still owes the notice. The notice names the redacted reason of the last write
/// that really failed, and never the model-addressed text of a rejection.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnClosingNoticesNothingExecutedTests
{
    private const string WriteSkill = "create_contract";
    private const string OtherWriteSkill = "update_contract";
    private const string PreviewApplySkill = "fill_group_by_criteria";
    private const string ReadSkill = "list_contracts";
    private const string ExplainSkill = "explain_contract_setup";
    private const string FalseSuccess = "Erledigt – ich habe den Vertrag angelegt.";
    private const string FalseSuccessWithQuestion = "Erledigt – ich habe den Vertrag angelegt. Noch etwas?";
    private const string HonestDenial = "Nein, ich habe nichts gespeichert, das hat nicht geklappt.";
    private const string Failure = "Error: no such contract.";
    private const string SecondFailure = "Error: the contract name is already taken.";
    private const string ModelAddressedRejection = "Rejected: this action did NOT run.";

    private static LLMFunctionCall Call(
        string name,
        bool success,
        bool confirmation = false,
        bool rejectedRepeat = false,
        bool rejectedOutside = false,
        bool rejectedByRecipeGuard = false,
        bool outside = false,
        bool skipped = false,
        string? result = null,
        bool apply = true)
    {
        var rejected = rejectedRepeat || rejectedOutside || rejectedByRecipeGuard;
        return new()
        {
            FunctionName = name,
            Parameters = new Dictionary<string, object> { ["apply"] = apply },
            Success = success,
            Result = result ?? (rejected ? ModelAddressedRejection : success ? "ok" : Failure),
            RequiresConfirmation = confirmation,
            IsRejectedRepeat = rejectedRepeat,
            IsRejectedOutsideToolset = rejectedOutside,
            IsRejectedByReadOnlyRecipeGuard = rejectedByRecipeGuard,
            IsOutsideToolset = outside,
            SkippedByStop = skipped
        };
    }

    private static string Notice(string language)
    {
        GracefulCorrectionTexts.TryGetText(GracefulCorrectionTexts.RecipeNothingStoredNotice, language, out var text)
            .ShouldBeTrue();
        return RecipeEngineDefaults.NothingStoredNoticeSeparator + text;
    }

    private static string NoticeWithReason(string language, string reason) =>
        Notice(language) + RecipeEngineDefaults.NothingStoredFailureReasonSeparator + reason;

    [Test]
    public void FailedWriteWithSuccessProse_OwesTheLocalizedNoticeAndTheReason()
    {
        var calls = new[] { Call(WriteSkill, success: false) };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void FailedWrite_NoticeIsLocalizedToTheTurnLanguage()
    {
        var calls = new[] { Call(WriteSkill, success: false) };

        var german = TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de");
        var french = TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "fr");

        german.ShouldNotBe(french);
        french.ShouldBe(NoticeWithReason("fr", Failure));
    }

    [Test]
    public void TheReason_IsTheLastWriteThatReallyFailed_AndIsRedacted()
    {
        var calls = new[]
        {
            Call(WriteSkill, success: false, result: Failure),
            Call(OtherWriteSkill, success: false, result: "Error: update_contract refused the name.")
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de")
            .ShouldBe(NoticeWithReason("de", "Error: " + MutationGuardConstants.RedactedInternalIdentifier + " refused the name."));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void FailedWriteWithoutAResultText_OwesTheNoticeAlone(string? result)
    {
        var call = Call(WriteSkill, success: false);
        call.Result = result;

        TurnClosingNotices.NothingExecuted(FalseSuccess, [call], "de").ShouldBe(Notice("de"));
    }

    [TestCase("Nein, ich habe nichts gespeichert, das hat nicht geklappt.")]
    [TestCase("No, nothing has been saved.")]
    [TestCase("Non, rien n'a été enregistré.")]
    public void FailedWriteWithAnHonestDenial_OwesNothing(string denial)
    {
        var calls = new[] { Call(WriteSkill, success: false) };

        TurnClosingNotices.NothingExecuted(denial, calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void FailedWriteWithSuccessProseThatEndsInAQuestion_StillOwesTheNotice()
    {
        var calls = new[] { Call(WriteSkill, success: false) };

        TurnClosingNotices.NothingExecuted(FalseSuccessWithQuestion, calls, "de")
            .ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void FailedWriteWithABlankAnswer_OwesNothingHere_BecauseTheStepFailedNoticeCarriesTheMessage()
    {
        var calls = new[] { Call(WriteSkill, success: false) };

        TurnClosingNotices.NothingExecuted("  ", calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void HeldConfirmationAlone_OwesNothing()
    {
        var calls = new[] { Call(WriteSkill, success: false, confirmation: true) };

        TurnClosingNotices.NothingExecuted("Bitte bestätige, dass ich den Vertrag anlegen soll.", calls, "de")
            .ShouldBeEmpty();
    }

    [Test]
    public void TwoHeldConfirmations_OweNothing()
    {
        var calls = new[]
        {
            Call(WriteSkill, success: false, confirmation: true),
            Call(OtherWriteSkill, success: false, confirmation: true)
        };

        TurnClosingNotices.NothingExecuted("Bitte bestätige beide Änderungen.", calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void HeldConfirmationNextToAFailedWrite_StillOwesTheNotice_BecauseTheFailedWriteStoredNothing()
    {
        var calls = new[]
        {
            Call(WriteSkill, success: false, confirmation: true),
            Call(OtherWriteSkill, success: false)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void HeldConfirmationNextToASuccessfulWrite_OwesNothing()
    {
        var calls = new[]
        {
            Call(WriteSkill, success: false, confirmation: true),
            Call(OtherWriteSkill, success: true)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void PartialSuccess_OneWriteSucceeded_OwesNothing()
    {
        var calls = new[]
        {
            Call(WriteSkill, success: true),
            Call(OtherWriteSkill, success: false)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void SuccessfulReadNextToAFailedWrite_StillOwesTheNotice()
    {
        var calls = new[]
        {
            Call(ReadSkill, success: true),
            Call(WriteSkill, success: false)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void FailedReadOnly_OwesNothing_BecauseNothingWasMeantToBeStored()
    {
        var calls = new[] { Call(ReadSkill, success: false) };

        TurnClosingNotices.NothingExecuted("Hier ist die Liste.", calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void FailedCallOfASkillWithAReadEffect_IsNotAWrite()
    {
        var calls = new[] { Call(ExplainSkill, success: false) };
        var functions = new List<LLMFunction> { new() { Name = ExplainSkill, Effect = SkillEffect.Explain } };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de", functions).ShouldBeEmpty();
    }

    [Test]
    public void FailedCallOfASkillWithAMutateEffect_IsAWrite_WhateverItsName()
    {
        var calls = new[] { Call("open_something", success: false) };
        var functions = new List<LLMFunction> { new() { Name = "open_something", Effect = SkillEffect.Mutate } };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de", functions)
            .ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void NoCalls_OwesNothing()
    {
        TurnClosingNotices.NothingExecuted(FalseSuccess, [], "de").ShouldBeEmpty();
    }

    [Test]
    public void CallSkippedByTheStop_IsNoAttempt_AndOwesNothing()
    {
        var call = Call(WriteSkill, success: false, skipped: true);

        TurnClosingNotices.NothingExecuted(FalseSuccess, [call], "de").ShouldBeEmpty();
    }

    [TestCase("rejectedRepeat")]
    [TestCase("rejectedOutside")]
    [TestCase("rejectedByRecipeGuard")]
    public void WriteRejectedWithoutRunning_IsAFailedAttempt_AndOwesTheNoticeWithoutTheRejectionText(string kind)
    {
        var call = Call(
            WriteSkill,
            success: false,
            rejectedRepeat: kind == "rejectedRepeat",
            rejectedOutside: kind == "rejectedOutside",
            rejectedByRecipeGuard: kind == "rejectedByRecipeGuard");

        var notice = TurnClosingNotices.NothingExecuted(FalseSuccess, [call], "de");

        notice.ShouldBe(Notice("de"));
        notice.ShouldNotContain("Rejected");
    }

    [TestCase("rejectedRepeat")]
    [TestCase("rejectedOutside")]
    [TestCase("rejectedByRecipeGuard")]
    public void RealFailureBesideARejectedWrite_NamesTheRealFailure_NotTheRejection(string kind)
    {
        var calls = new[]
        {
            Call(WriteSkill, success: false, result: SecondFailure),
            Call(
                OtherWriteSkill,
                success: false,
                rejectedRepeat: kind == "rejectedRepeat",
                rejectedOutside: kind == "rejectedOutside",
                rejectedByRecipeGuard: kind == "rejectedByRecipeGuard")
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", SecondFailure));
    }

    [Test]
    public void ObservedOutsideWriteThatFailed_RanAndStoredNothing_SoItOwesTheNotice()
    {
        var call = Call(WriteSkill, success: false, outside: true);

        TurnClosingNotices.NothingExecuted(FalseSuccess, [call], "de").ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void ObservedOutsideWriteThatSucceeded_NextToAFailedOfferedWrite_OwesNothing()
    {
        var calls = new[]
        {
            Call("delete_client", success: true, outside: true),
            Call(WriteSkill, success: false)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void MembershipRejectedWrite_NextToAFailedOfferedWrite_StillOwesTheNotice()
    {
        var calls = new[]
        {
            Call("delete_client", success: false, rejectedOutside: true, outside: true),
            Call(WriteSkill, success: false)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void RealFailureBesideARejectedRepeat_OwesTheNotice()
    {
        var calls = new[]
        {
            Call(WriteSkill, success: false),
            Call(WriteSkill, success: false, rejectedRepeat: true)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void RejectedRepeatOfAWriteThatSucceededEarlier_OwesNothing()
    {
        var calls = new[]
        {
            Call(WriteSkill, success: true),
            Call(WriteSkill, success: false, rejectedRepeat: true)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void PreviewSucceeded_ThenApplyFailed_OwesTheNotice()
    {
        var calls = new[]
        {
            Call(PreviewApplySkill, success: true, apply: false),
            Call(PreviewApplySkill, success: false, result: SecondFailure)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", SecondFailure));
    }

    [Test]
    public void PreviewSucceeded_ThenApplyRejectedAsARepeat_OwesTheNotice()
    {
        var calls = new[]
        {
            Call(PreviewApplySkill, success: true, apply: false),
            Call(PreviewApplySkill, success: false, rejectedRepeat: true)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(Notice("de"));
    }

    [Test]
    public void PreviewSucceeded_ThenApplySucceeded_OwesNothing()
    {
        var calls = new[]
        {
            Call(PreviewApplySkill, success: true, apply: false),
            Call(PreviewApplySkill, success: true, apply: true)
        };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void PreviewAlone_OwesNothing_BecauseItIsNeitherAnAttemptNorAFailure()
    {
        var calls = new[] { Call(PreviewApplySkill, success: true, apply: false) };

        TurnClosingNotices.NothingExecuted("Das wären 5 Personen. Soll ich das umsetzen?", calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void PreviewThatFailed_IsAFailedAttempt()
    {
        var calls = new[] { Call(PreviewApplySkill, success: false, apply: false) };

        TurnClosingNotices.NothingExecuted(FalseSuccess, calls, "de").ShouldBe(NoticeWithReason("de", Failure));
    }

    [Test]
    public void NothingChanged_ReadOnlyRecipeClaim_ReturnsTheNoticeOnce()
    {
        var calls = new[] { Call(WriteSkill, success: false) };

        var notice = TurnClosingNotices.NothingChanged(true, FalseSuccess, calls, "de");

        notice.ShouldBe(Notice("de"));
    }

    [Test]
    public void Collect_FailedWriteWithSuccessProse_AppendsTheNoticeWithTheReason()
    {
        var logger = new RecordingLogger<TurnClosingNoticesNothingExecutedTests>();
        var calls = new[] { Call(WriteSkill, success: false) };

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, FalseSuccess, calls, recipePausedOnAsk: false, logger,
            language: "de");

        notices.ShouldBe([NoticeWithReason("de", Failure)]);
    }

    [Test]
    public void Collect_FailedWriteWithHonestProse_AppendsNothing()
    {
        var logger = new RecordingLogger<TurnClosingNoticesNothingExecutedTests>();
        var calls = new[] { Call(WriteSkill, success: false) };

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, HonestDenial, calls, recipePausedOnAsk: false, logger,
            language: "de");

        notices.ShouldBeEmpty();
    }

    [Test]
    public void Collect_FailedWriteWithBlankProse_KeepsTheStepFailedNoticeAndAddsNoSecondOne()
    {
        var logger = new RecordingLogger<TurnClosingNoticesNothingExecutedTests>();
        var failed = Call(WriteSkill, success: false);

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, string.Empty, [failed], recipePausedOnAsk: false, logger,
            language: "de");

        notices.ShouldBe([TurnClosingNotices.StepFailed(failed)]);
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning);
    }

    [Test]
    public void Collect_OnlyRejectedWritesWithBlankProse_SurfaceNoModelAddressedText()
    {
        var logger = new RecordingLogger<TurnClosingNoticesNothingExecutedTests>();
        var rejected = Call(WriteSkill, success: false, rejectedOutside: true);

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, string.Empty, [rejected], recipePausedOnAsk: false, logger,
            language: "de");

        notices.ShouldNotContain(notice => notice.Contains("Rejected"));
    }

    [Test]
    public void Collect_PartialSuccess_AppendsNothing()
    {
        var logger = new RecordingLogger<TurnClosingNoticesNothingExecutedTests>();
        var calls = new[]
        {
            Call(WriteSkill, success: true),
            Call(OtherWriteSkill, success: false)
        };

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, FalseSuccess, calls, recipePausedOnAsk: false, logger,
            language: "de");

        notices.ShouldBeEmpty();
    }

    [Test]
    public void Collect_ReadOnlyRecipeClaimWithAFailedWrite_AddsTheSentenceOnlyOnce()
    {
        var logger = new RecordingLogger<TurnClosingNoticesNothingExecutedTests>();
        var calls = new[] { Call(WriteSkill, success: false) };

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, FalseSuccess, calls, recipePausedOnAsk: false, logger,
            readOnlyRecipeCompleted: true, language: "de");

        notices.ShouldBe([Notice("de")]);
    }
}
