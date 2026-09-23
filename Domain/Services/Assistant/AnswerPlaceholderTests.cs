// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Recognition and construction of the tool-call stand-in text. The history sentence must name the called
/// tools without any bracket, and every retired bracketed literal ("[Executing function calls]",
/// "[no action taken]", "[gathering data]") as well as the current sentence must be recognised when a model echoes them - alone, repeated or surrounded by whitespace -
/// while ordinary answers, including ones that merely start with a bracket, stay untouched.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AnswerPlaceholderTests
{
    private const string Note = "(Called tools: get_employee, list_groups. Their results follow.)";

    private static LLMFunctionCall Call(string name) => new() { FunctionName = name };

    [Test]
    public void ForToolCallTurn_WithoutProse_NamesTheToolsOnceInCallOrderWithoutBrackets()
    {
        var text = AnswerPlaceholder.ForToolCallTurn(
            string.Empty, new[] { Call("get_employee"), Call("list_groups"), Call("GET_EMPLOYEE") });

        text.ShouldBe(Note);
        text.ShouldNotContain("[");
        text.ShouldNotBe(LLMLoopConstants.ExecutingFunctionCallsPlaceholder);
    }

    [Test]
    public void ForToolCallTurn_WithProse_KeepsTheProse()
    {
        AnswerPlaceholder.ForToolCallTurn("Let me check.", new[] { Call("get_employee") }).ShouldBe("Let me check.");
    }

    [Test]
    public void ForToolCallTurn_WithAnEchoedPlaceholderAsProse_UsesTheNeutralSentence()
    {
        AnswerPlaceholder.ForToolCallTurn(LLMLoopConstants.ExecutingFunctionCallsPlaceholder, new[] { Call("get_employee") })
            .ShouldBe("(Called tools: get_employee. Their results follow.)");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  \n ")]
    [TestCase("[Executing function calls]")]
    [TestCase("  [executing function calls]\n")]
    [TestCase("[Executing function calls][Executing function calls]")]
    [TestCase("[Executing function calls]\n[Executing function calls] ")]
    [TestCase(Note)]
    [TestCase(Note + "\n" + Note)]
    [TestCase("[Executing function calls] " + Note)]
    [TestCase(LLMLoopConstants.NoActionTakenPlaceholder)]
    [TestCase("  [No Action Taken]\n")]
    [TestCase(LLMLoopConstants.GatheringDataPlaceholder)]
    [TestCase("[gathering data] [no action taken] " + Note)]
    public void BlankOrPlaceholderOnly_IsRecognised(string? answer)
    {
        AnswerPlaceholder.IsBlankOrPlaceholder(answer).ShouldBeTrue();
        AnswerPlaceholder.Visible(answer).ShouldBeEmpty();
    }

    [TestCase("Anna works today.")]
    [TestCase("[REPLIES:yes|no]")]
    [TestCase("(Note: Anna is absent.)")]
    [TestCase("The status was [Executing function calls] before.")]
    public void OrdinaryAnswers_AreNotPlaceholders(string answer)
    {
        AnswerPlaceholder.IsBlankOrPlaceholder(answer).ShouldBeFalse();
        AnswerPlaceholder.Visible(answer).ShouldBe(answer);
    }

    [Test]
    public void Visible_StripsALeadingEchoFromARealAnswer()
    {
        AnswerPlaceholder.Visible("[Executing function calls]\nAnna works today.").ShouldBe("Anna works today.");
    }

    [TestCase(LLMLoopConstants.NoActionTakenPlaceholder)]
    [TestCase(LLMLoopConstants.GatheringDataPlaceholder)]
    public void Visible_StripsALeadingRetiredLiteralFromARealAnswer(string retired)
    {
        AnswerPlaceholder.Visible(retired + " Anna works today.").ShouldBe("Anna works today.");
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("[")]
    [TestCase("[Executing func")]
    [TestCase("(")]
    [TestCase("(Called tools: get_empl")]
    [TestCase("[Executing function calls] (Called")]
    [TestCase("[no act")]
    [TestCase("[gathering d")]
    public void CouldBecomePlaceholder_WhileEveryCharacterStillMatches(string streamed)
    {
        AnswerPlaceholder.CouldBecomePlaceholder(streamed).ShouldBeTrue();
    }

    [TestCase("[R")]
    [TestCase("(N")]
    [TestCase("Hello")]
    [TestCase("(Called tools: get_employee) and more")]
    [TestCase("[Executing function calls] Hello")]
    public void CouldBecomePlaceholder_FalseOnceTheTextDiverges(string streamed)
    {
        AnswerPlaceholder.CouldBecomePlaceholder(streamed).ShouldBeFalse();
    }

    [Test]
    public void PlaceholderEchoFilter_DropsAPureEcho()
    {
        var filter = new PlaceholderEchoFilter();

        filter.Push("[Executing ").ShouldBeEmpty();
        filter.Push("function calls]").ShouldBeEmpty();
        filter.Flush().ShouldBeEmpty();
    }

    [Test]
    public void PlaceholderEchoFilter_ReleasesHeldTextOnDivergenceAndPassesEverythingAfterwards()
    {
        var filter = new PlaceholderEchoFilter();

        filter.Push("[").ShouldBeEmpty();
        filter.Push("R").ShouldBe("[R");
        filter.Push("EPLIES").ShouldBe("EPLIES");
        filter.Flush().ShouldBeEmpty();
    }
}
