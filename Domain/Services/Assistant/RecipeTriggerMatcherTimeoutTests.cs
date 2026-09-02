// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the containment of a regex timeout in the recipe trigger matcher. The trigger check runs on
/// every chat turn and RegexMatchTimeoutException is caught nowhere above it, so an escaping one would
/// end the turn with an error instead of simply engaging no recipe. The stems are escaped literals in a
/// plain alternation, so no input can provoke the timeout — the budget is passed explicitly instead.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTriggerMatcherTimeoutTests
{
    private const string Message =
        "Erstelle bitte einen neuen Mitarbeiter und trage seine Verfügbarkeit für den ganzen Monat ein";

    private static readonly string[] Stems = ["erstell", "anleg", "trag", "erfass", "einstell"];

    private static readonly TimeSpan Unmeetable = TimeSpan.FromTicks(1);

    [Test]
    public void RegexTimeout_IsContained_AndReportedAsNoMatch()
    {
        var logger = Substitute.For<ILogger>();

        var matched = RecipeTriggerMatcher.MatchesWordStart(Stems, Message, logger, Unmeetable);

        matched.ShouldBeFalse();
    }

    [Test]
    public void RegexTimeout_IsLogged_SoTheIncidentIsNotSilent()
    {
        var logger = Substitute.For<ILogger>();

        RecipeTriggerMatcher.MatchesWordStart(Stems, Message, logger, Unmeetable);

        logger.ReceivedWithAnyArgs(1).Log(
            default, default, default!, default, default!);
    }

    [Test]
    public void RegexTimeout_WithoutALogger_StillSurvives()
    {
        // Two of the four call sites (CompetingSkillIntentDetector, TurnReplayService) pass no logger.
        // Containment must not depend on one being there.
        var matched = RecipeTriggerMatcher.MatchesWordStart(Stems, Message, null, Unmeetable);

        matched.ShouldBeFalse();
    }

    [Test]
    public void StartsWith_RegexTimeout_IsContained_AndReportedAsNoMatch()
    {
        // The whole-word branch of MatchesStartsWith is the second regex on the per-turn path and needs
        // the same containment proof as MatchesWordStart.
        var logger = Substitute.For<ILogger>();

        var matched = RecipeTriggerMatcher.MatchesStartsWith(
            ["erstelle ", "lege "], Message, logger, Unmeetable);

        matched.ShouldBeFalse();
        logger.ReceivedWithAnyArgs(1).Log(default, default, default!, default, default!);
    }

    [Test]
    public void StartsWith_WithAWorkableBudget_TheSameInputStillMatches()
    {
        var matched = RecipeTriggerMatcher.MatchesStartsWith(
            ["erstelle ", "lege "], Message, null, TimeSpan.FromSeconds(1));

        matched.ShouldBeTrue();
    }

    [Test]
    public void WithAWorkableBudget_TheSameInputStillMatches()
    {
        // Counterpart: the timeout branch must be the reason for the false above, not a broken pattern.
        var logger = Substitute.For<ILogger>();

        var matched = RecipeTriggerMatcher.MatchesWordStart(
            Stems, Message, logger, TimeSpan.FromSeconds(1));

        matched.ShouldBeTrue();
        logger.DidNotReceiveWithAnyArgs().Log(default, default, default!, default, default!);
    }
}
