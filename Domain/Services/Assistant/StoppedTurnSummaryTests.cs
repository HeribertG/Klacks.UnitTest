// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// What a stopped turn tells the user it did. The client is told about data changes only - successful,
/// non-repeatable server calls of skills whose effect is Mutate - because "nothing was executed" means
/// "nothing was changed"; lookups, explanations, advice, navigations, held confirmations, failed calls,
/// repeats the loop rejected, calls the stop skipped and everything that is handed to the browser are not
/// listed. An action without a label in the user's language is counted but not named, so the client can tell
/// "nothing ran" from "something ran that cannot be named". Labels and count come from the same list.
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
    private const string ExplainSkill = "explain_page_schedule";
    private const string ExplainLabel = "Dienstplan-Seite erklären";
    private const string CountReadSkill = "count_open_shifts";
    private const string AdviseSkill = "recommend_shift_cover";
    private const string NavigationSkill = "open_schedule";
    private const string NavigationLabel = "Dienstplan öffnen";
    private const string NoEffectSkill = "legacy_bridge_skill";
    private const string NoEffectLabel = "Alte Aktion";

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
    public void AnExplainSkillWithoutAReadOnlyPrefix_IsNotAnActionAndLeavesNoLabel()
    {
        var summary = StoppedTurnSummary.From(Context("de"), [Ran(ExplainSkill)]);

        summary.Labels.ShouldBeEmpty();
        summary.ExecutedCount.ShouldBe(0);
    }

    [TestCase(CountReadSkill)]
    [TestCase(AdviseSkill)]
    public void AReadOrAdviseSkillWithoutAReadOnlyPrefix_IsNotAnAction(string skill)
    {
        var summary = StoppedTurnSummary.From(Context("de"), [Ran(skill)]);

        summary.Labels.ShouldBeEmpty();
        summary.ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void ANavigationResult_IsNotAnActionEvenWhenTheSkillIsSeededAsMutate()
    {
        var navigation = Ran(NavigationSkill);
        navigation.IsNavigation = true;

        var summary = StoppedTurnSummary.From(Context("de"), [navigation]);

        summary.Labels.ShouldBeEmpty();
        summary.ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void AWriteBesideAnExplainAndANavigation_IsTheOnlyActionOfTheSummary()
    {
        var navigation = Ran(NavigationSkill);
        navigation.IsNavigation = true;

        var summary = StoppedTurnSummary.From(Context("de"), [Ran(ExplainSkill), navigation, Ran(WriteSkill), Ran(ReadSkill)]);

        summary.Labels.ShouldBe([GermanLabel]);
        summary.ExecutedCount.ShouldBe(1);
    }

    [Test]
    public void AWriteThatReturnedData_IsStillNamed()
    {
        var write = Ran(WriteSkill);
        write.ResultKind = LLMFunctionResultKind.Data;
        write.DataJson.Add("{\"Route\":\"/workplace/client\"}");

        var summary = StoppedTurnSummary.From(Context("de"), [write]);

        summary.Labels.ShouldBe([GermanLabel]);
        summary.ExecutedCount.ShouldBe(1);
    }

    [Test]
    public void ASkillWithoutAnEffect_IsTreatedAsAWriteBecauseNothingSaysItIsSafe()
    {
        var summary = StoppedTurnSummary.From(Context("de"), [Ran(NoEffectSkill)]);

        summary.Labels.ShouldBe([NoEffectLabel]);
        summary.ExecutedCount.ShouldBe(1);
    }

    [Test]
    public void ANonWriteWithoutALabelInTheUsersLanguage_DoesNotMakeTheClientThinkSomethingRan()
    {
        var summary = StoppedTurnSummary.From(Context("fr"), [Ran(ExplainSkill), Ran(CountReadSkill)]);

        summary.Labels.ShouldBeEmpty();
        summary.ExecutedCount.ShouldBe(0);
    }

    [Test]
    public void LabelsAndCount_ComeFromTheSameListOfWrites()
    {
        var navigation = Ran(NavigationSkill);
        navigation.IsNavigation = true;
        var calls = new[] { Ran(ExplainSkill), Ran(WriteSkill), navigation, Ran(SecondWriteSkill), Ran(NoEffectSkill), Ran(CountReadSkill) };

        var summary = StoppedTurnSummary.From(Context("de"), calls);

        summary.Labels.ShouldBe([GermanLabel, MembershipLabel, NoEffectLabel]);
        summary.ExecutedCount.ShouldBe(summary.Labels.Count);
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
                Effect = SkillEffect.Mutate,
                Labels = new Dictionary<string, string> { ["de"] = GermanLabel, ["en"] = "Create employee" }
            },
            new LLMFunction
            {
                Name = SecondWriteSkill,
                Effect = SkillEffect.Mutate,
                Labels = new Dictionary<string, string> { ["de"] = MembershipLabel }
            },
            new LLMFunction { Name = ReadSkill, Effect = SkillEffect.Read },
            new LLMFunction
            {
                Name = ExplainSkill,
                Effect = SkillEffect.Explain,
                Labels = new Dictionary<string, string> { ["de"] = ExplainLabel }
            },
            new LLMFunction { Name = CountReadSkill, Effect = SkillEffect.Read },
            new LLMFunction { Name = AdviseSkill, Effect = SkillEffect.Advise },
            new LLMFunction
            {
                Name = NavigationSkill,
                Effect = SkillEffect.Mutate,
                Labels = new Dictionary<string, string> { ["de"] = NavigationLabel }
            },
            new LLMFunction
            {
                Name = NoEffectSkill,
                Labels = new Dictionary<string, string> { ["de"] = NoEffectLabel }
            }
        ]
    };
}
