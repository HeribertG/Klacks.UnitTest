// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the selection rule behind the "every step failed" notice, which both chat loops carried inline.
/// The notice exists for the case where a forced step failed on every iteration and the turn therefore has
/// no prose at all; a rejected repeat carries only the generic rejection text, so the genuine failure of an
/// earlier iteration is the message worth showing. Any prose at all suppresses the notice, because then the
/// user already has an answer.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
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
    public void StepFailedText_CarriesThePrefixAndTheRedactedResult()
    {
        var notice = TurnClosingNotices.StepFailed(Call("a", success: false, RealFailure));

        notice.ShouldStartWith(Klacks.Api.Domain.Constants.MutationGuardConstants.RecipeStepFailedNoticePrefix);
        notice.ShouldContain("Nightshift");
    }
}
