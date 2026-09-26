// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the selection rule behind the "every step failed" notice, which both chat loops carried inline.
/// The notice exists for the case where a forced step failed on every iteration and the turn therefore has
/// no prose at all; a rejected repeat carries only the generic rejection text, so the genuine failure of an
/// earlier iteration is the message worth showing. Any prose at all suppresses the notice, because then the
/// user already has an answer.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnClosingNoticesTests
{
    private const string RealFailure = "No contract named 'Nightshift' exists. Available: A, B.";
    private const string RejectionText = "This skill was already called in this turn.";

    private static LLMFunctionCall Call(string name, bool success, string result, bool rejectedRepeat = false) =>
        new() { FunctionName = name, Success = success, Result = result, IsRejectedRepeat = rejectedRepeat };

    [Test]
    public void GenuineFailure_IsPreferredOverALaterRejectedRepeat()
    {
        var calls = new List<LLMFunctionCall>
        {
            Call("resolve_contract", success: false, RealFailure),
            Call("resolve_contract", success: false, RejectionText, rejectedRepeat: true)
        };

        TurnClosingNotices.LastUnrecoveredFailure(calls, string.Empty)!.Result.ShouldBe(RealFailure);
    }

    [Test]
    public void AllCallsRejectedRepeats_FallsBackToTheLastCall()
    {
        var calls = new List<LLMFunctionCall>
        {
            Call("a", success: false, "first", rejectedRepeat: true),
            Call("b", success: false, "last", rejectedRepeat: true)
        };

        TurnClosingNotices.LastUnrecoveredFailure(calls, string.Empty)!.Result.ShouldBe("last");
    }

    [Test]
    public void AnySuccessfulCall_SuppressesTheNotice()
    {
        var calls = new List<LLMFunctionCall>
        {
            Call("a", success: true, "ok"),
            Call("b", success: false, RealFailure)
        };

        TurnClosingNotices.LastUnrecoveredFailure(calls, string.Empty).ShouldBeNull();
    }

    [Test]
    public void AnswerWithProse_SuppressesTheNotice()
    {
        var calls = new List<LLMFunctionCall> { Call("a", success: false, RealFailure) };

        TurnClosingNotices.LastUnrecoveredFailure(calls, "Here is what I found.").ShouldBeNull();
    }

    [Test]
    public void NoCallsAtAll_SuppressesTheNotice()
    {
        TurnClosingNotices.LastUnrecoveredFailure(new List<LLMFunctionCall>(), string.Empty).ShouldBeNull();
    }

    [Test]
    public void Collect_MutationRequestWithoutAnyCall_YieldsOnlyTheNoActionNotice()
    {
        var logger = new RecordingLogger<TurnClosingNoticesTests>();

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, "I looked into it.", [], recipePausedOnAsk: false, logger);

        notices.ShouldBe([MutationGuardConstants.NoActionStreamNotice]);
        logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public void Collect_AnswerThatOwesNothing_YieldsNoNotice()
    {
        var logger = new RecordingLogger<TurnClosingNoticesTests>();

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: false, forceConfirmation: false, "Hello.", [], recipePausedOnAsk: false, logger);

        notices.ShouldBeEmpty();
    }

    [Test]
    public void Collect_EveryCallFailedAndNoProse_YieldsTheStepFailedNoticeAndLogsTheRawResult()
    {
        var logger = new RecordingLogger<TurnClosingNoticesTests>();
        var failed = Call("resolve_contract", success: false, RealFailure);

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, string.Empty, [failed], recipePausedOnAsk: false, logger);

        notices.ShouldBe([TurnClosingNotices.StepFailed(failed)]);
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains(RealFailure));
    }

    [Test]
    public void Collect_RecipePausedOnAsk_SuppressesTheNoActionNotice()
    {
        var logger = new RecordingLogger<TurnClosingNoticesTests>();

        var notices = TurnClosingNotices.Collect(
            isMutationIntent: true, forceConfirmation: false, "Which contract?", [], recipePausedOnAsk: true, logger);

        notices.ShouldBeEmpty();
    }

    [Test]
    public void StepFailedText_CarriesThePrefixAndTheRedactedResult()
    {
        var notice = TurnClosingNotices.StepFailed(Call("a", success: false, RealFailure));

        notice.ShouldStartWith(Klacks.Api.Domain.Constants.MutationGuardConstants.RecipeStepFailedNoticePrefix);
        notice.ShouldContain("Nightshift");
    }

    private const string StoredClaim = "Erledigt – ich habe 3 Tage als Abschlussfrist gespeichert.";

    [Test]
    public void NothingStored_ClaimAfterReadOnlyRecipe_AppendsTheLocalizedNotice()
    {
        var calls = new List<LLMFunctionCall> { Call("get_period_close_schedule", success: true, "Data") };

        var notice = TurnClosingNotices.NothingStored(true, StoredClaim, calls, "de");

        GracefulCorrectionTexts.TryGetText(GracefulCorrectionTexts.RecipeNothingStoredNotice, "de", out var german)
            .ShouldBeTrue();
        notice.ShouldBe(RecipeEngineDefaults.NothingStoredNoticeSeparator + german);
    }

    [Test]
    public void NothingStored_WithoutReadOnlyRecipe_IsEmpty()
    {
        TurnClosingNotices.NothingStored(false, StoredClaim, new List<LLMFunctionCall>(), "de").ShouldBeEmpty();
    }

    [Test]
    public void NothingStored_HonestOffer_IsEmpty()
    {
        TurnClosingNotices.NothingStored(
                true, "Bern schliesst am 05.10. Soll ich 3 Tage speichern?", new List<LLMFunctionCall>(), "de")
            .ShouldBeEmpty();
    }

    [Test]
    public void NothingStored_AuxiliaryAndParticipleInDifferentSentences_IsNoClaim()
    {
        const string honest = "Es ist noch kein Nachlauf gespeichert, deshalb schliesst Klacksy nichts ab. "
            + "Dazu müssten alle Admins «Voll autonom» gewählt haben.";

        TurnClosingNotices.NothingStored(true, honest, new List<LLMFunctionCall>(), "de").ShouldBeEmpty();
    }

    [Test]
    public void NothingStored_SuccessfulWriteInTheTurn_IsEmpty()
    {
        var calls = new List<LLMFunctionCall> { Call("set_period_close_lag", success: true, "stored") };

        TurnClosingNotices.NothingStored(true, StoredClaim, calls, "de").ShouldBeEmpty();
    }

    [Test]
    public void Collect_ReadOnlyRecipeClaim_AddsTheNothingStoredNotice()
    {
        var calls = new List<LLMFunctionCall> { Call("get_period_close_schedule", success: true, "Data") };

        var notices = TurnClosingNotices.Collect(
            false, false, StoredClaim, calls, false, Substitute.For<ILogger>(), readOnlyRecipeCompleted: true,
            language: "en");

        notices.ShouldBe(new[] { RecipeEngineDefaults.NothingStoredNoticeSeparator + RecipeEngineDefaults.NothingStoredNotice });
    }
}
