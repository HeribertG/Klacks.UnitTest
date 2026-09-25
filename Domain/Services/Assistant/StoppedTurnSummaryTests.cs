// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// What a stopped turn tells the user it did. The client is told about write actions only - successful,
/// non-repeatable server calls - because "nothing was executed" means "nothing was changed"; lookups,
/// held confirmations, failed calls, repeats the loop rejected, calls the stop skipped and everything that
/// is handed to the browser are not listed. An action without a label in the user's language is counted but
/// not named, so the client can tell "nothing ran" from "something ran that cannot be named".
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class StoppedTurnSummaryTests
{
    private const string WriteSkill = "create_employee";
    private const string SecondWriteSkill = "update_membership";
    private const string ReadSkill = "search_employees";
    private const string GermanLabel = "Mitarbeiter anlegen";
    private const string MembershipLabel = "Zugehörigkeit ändern";

    [Test]
    public void ASuccessfulWrite_IsListedWithItsLabelInTheUsersLanguage()
    {
        var summary = StoppedTurnSummary.From(Context("de"), [Ran(WriteSkill)]);

        summary.Labels.ShouldBe([GermanLabel]);
        summary.ExecutedCount.ShouldBe(1);
    }

    [Test]
    public void AWriteWithoutALabelInTheUsersLanguage_IsCountedButNotNamed()
    {
        var summary = StoppedTurnSummary.From(Context("fr"), [Ran(WriteSkill)]);

        summary.Labels.ShouldBeEmpty();
        summary.ExecutedCount.ShouldBe(1);
    }

    [Test]
    public void ATurnThatRanNothing_HasNoActionAtAll()
    {
        var summary = StoppedTurnSummary.From(Context("de"), []);

        summary.Labels.ShouldBeEmpty();
        summary.ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void TwoDifferentWrites_AreListedInCallOrder()
    {
        var summary = StoppedTurnSummary.From(Context("de"), [Ran(SecondWriteSkill), Ran(WriteSkill)]);

        summary.Labels.ShouldBe([MembershipLabel, GermanLabel]);
        summary.ExecutedCount.ShouldBe(2);
    }

    [Test]
    public void TheSameWriteTwiceInOneRound_IsOneActionOfTheSummary()
    {
        var summary = StoppedTurnSummary.From(Context("de"), [Ran(WriteSkill), Ran(WriteSkill)]);

        summary.Labels.ShouldBe([GermanLabel]);
        summary.ExecutedCount.ShouldBe(1);
    }

    [Test]
    public void ALookup_IsNotAnAction()
    {
        StoppedTurnSummary.From(Context("de"), [Ran(ReadSkill)]).ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void ACallTheStopSkipped_IsNotAnAction()
    {
        var skipped = Ran(WriteSkill);
        skipped.SkippedByStop = true;
        skipped.Success = false;

        StoppedTurnSummary.From(Context("de"), [skipped]).ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void ACallThatWasRegisteredButNeverProducedAResult_IsNotAnAction()
    {
        var pending = new LLMFunctionCall { FunctionName = WriteSkill };

        StoppedTurnSummary.From(Context("de"), [pending]).ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void AFailedWrite_AHeldConfirmationAndARejectedRepeat_AreNotActions()
    {
        var failed = Ran(WriteSkill);
        failed.Success = false;
        var held = Ran(WriteSkill);
        held.Success = false;
        held.RequiresConfirmation = true;
        var repeat = Ran(WriteSkill);
        repeat.Success = false;
        repeat.IsRejectedRepeat = true;

        StoppedTurnSummary.From(Context("de"), [failed, held, repeat]).ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void AUiActionAndAUiPassthroughCall_AreNotActionsBecauseTheBrowserNeverReceivedThem()
    {
        var uiAction = Ran(WriteSkill);
        uiAction.UiActionSteps = "[{\"type\":\"click\"}]";
        var passthrough = Ran(SecondWriteSkill);
        passthrough.ResultKind = LLMFunctionResultKind.UiPassthrough;

        StoppedTurnSummary.From(Context("de"), [uiAction, passthrough]).ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void ExecutedCalls_KeepsFailedAndHeldCallsButDropsTheOnesThatNeverRan()
    {
        var failed = Ran(WriteSkill);
        failed.Success = false;
        var held = Ran(SecondWriteSkill);
        held.RequiresConfirmation = true;
        var skipped = Ran(ReadSkill);
        skipped.SkippedByStop = true;
        var repeat = Ran(ReadSkill);
        repeat.IsRejectedRepeat = true;
        var uiAction = Ran(ReadSkill);
        uiAction.UiActionSteps = "[]";
        var passthrough = Ran(ReadSkill);
        passthrough.ResultKind = LLMFunctionResultKind.UiPassthrough;
        var pending = new LLMFunctionCall { FunctionName = ReadSkill };

        var executed = StoppedTurnSummary.ExecutedCalls([failed, held, skipped, repeat, uiAction, passthrough, pending]);

        executed.ShouldBe([failed, held]);
    }

    [Test]
    public void AContextWithoutAToolset_StillCountsTheWrite()
    {
        var summary = StoppedTurnSummary.From(null, [Ran(WriteSkill)]);

        summary.Labels.ShouldBeEmpty();
        summary.ExecutedCount.ShouldBe(1);
    }

    [TestCase("", TurnInterruptionDefaults.InterruptedMarker)]
    [TestCase("   ", TurnInterruptionDefaults.InterruptedMarker)]
    [TestCase("Anna is", "Anna is\n" + TurnInterruptionDefaults.InterruptedMarker)]
    public void TheStoredAnswer_EndsWithTheMarkerTheModelReadsLater(string streamed, string expected)
    {
        StoppedTurnSummary.StoredAnswer(streamed).ShouldBe(expected);
    }

    private static LLMFunctionCall Ran(string skill) => new()
    {
        FunctionName = skill,
        Success = true,
        Result = "Done."
    };

    private static LLMContext Context(string language) => new()
    {
        Message = "Do it.",
        UserId = Guid.NewGuid().ToString(),
        Language = language,
        AvailableFunctions =
        [
            new LLMFunction
            {
                Name = WriteSkill,
                Labels = new Dictionary<string, string> { ["de"] = GermanLabel, ["en"] = "Create employee" }
            },
            new LLMFunction
            {
                Name = SecondWriteSkill,
                Labels = new Dictionary<string, string> { ["de"] = MembershipLabel }
            },
            new LLMFunction { Name = ReadSkill }
        ]
    };
}
