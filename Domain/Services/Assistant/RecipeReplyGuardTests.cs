// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RecipeReplyGuard — verifies that a well-formed native confirmation question passes
/// through untouched, while tool-call markup leaks and completion claims ("I updated the address.")
/// are replaced by the deterministic localized confirmation (using the recipe's goal translations when
/// available) or the localized ask question (falling back to the raw ask prompt without translations).
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeReplyGuardTests
{
    private const string Goal = "Create a new group.";
    private const string AlternativeGoal = "Add an existing shift/order to a named group.";

    private static readonly Dictionary<string, string> GoalTranslations = new()
    {
        ["de"] = "Eine neue Gruppe erstellen.",
        ["en"] = "Create a new group.",
        ["fr"] = "Créer un nouveau groupe.",
        ["it"] = "Creare un nuovo gruppo.",
    };

    private static readonly Dictionary<string, string> AlternativeGoalTranslations = new()
    {
        ["de"] = "Einen bestehenden Dienst einer Gruppe hinzufügen.",
        ["en"] = "Add an existing shift/order to a named group.",
        ["fr"] = "Ajouter un service existant à un groupe.",
        ["it"] = "Aggiungere un servizio esistente a un gruppo.",
    };

    private static readonly Dictionary<string, string> AskTranslations = new()
    {
        ["de"] = "Ab welchem Datum soll die Gruppe gültig sein?",
        ["en"] = "From which date should the group be valid?",
        ["fr"] = "À partir de quelle date le groupe doit-il être valable ?",
        ["it"] = "Da quale data deve essere valido il gruppo?",
    };

    [TestCase("Möchtest du eine neue Gruppe erstellen? Ja oder Nein?")]
    [TestCase("Do you want me to start onboarding a new employee?")]
    [TestCase("Veux-tu créer un nouveau groupe ?")]
    [TestCase("Vuoi creare un nuovo gruppo?")]
    public void SafeConfirmation_WellFormedQuestion_PassesThroughUntouched(string reply)
    {
        RecipeReplyGuard.SafeConfirmation(reply, Goal, null, "de").ShouldBe(reply);
    }

    [Test]
    public void SafeConfirmation_CjkQuestionMark_PassesThrough()
    {
        var reply = "要建立新的分組嗎？";
        RecipeReplyGuard.SafeConfirmation(reply, Goal, null, "zh-TW").ShouldBe(reply);
    }

    [Test]
    public void SafeConfirmation_CompletionClaim_ReplacedByDeterministicText()
    {
        // The exact live failure: the model claims the action is done instead of asking.
        var reply = "Ich aktualisiere die Sekretariatsadresse für dich.";

        var result = RecipeReplyGuard.SafeConfirmation(reply, Goal, null, "de");

        result.ShouldNotBe(reply);
        result.ShouldContain(Goal);
        result.ShouldContain("?");
    }

    [Test]
    public void SafeConfirmation_ToolMarkupLeak_StrippedAndReplaced()
    {
        var reply = "Ich aktualisiere das für dich. <function_calls><invoke name=\"update_owner_address\">"
                    + "<parameter name=\"street\">Bahnhofstrasse 1</parameter></invoke></function_calls>";

        var result = RecipeReplyGuard.SafeConfirmation(reply, Goal, null, "de");

        result.ShouldNotContain("function_calls");
        result.ShouldNotContain("invoke");
        result.ShouldContain(Goal);
        result.ShouldContain("?");
    }

    [Test]
    public void SafeConfirmation_ToolMarkupAroundRealQuestion_MarkupStrippedQuestionKept()
    {
        // Markup is stripped first; if a genuine question survives, it is kept (native quality wins).
        var reply = "<function_calls><invoke name=\"x\"></invoke></function_calls>Möchtest du die Gruppe erstellen?";

        var result = RecipeReplyGuard.SafeConfirmation(reply, Goal, null, "de");

        result.ShouldNotContain("function_calls");
        result.ShouldContain("Möchtest du die Gruppe erstellen?");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void SafeConfirmation_EmptyReply_ReplacedByDeterministicText(string? reply)
    {
        var result = RecipeReplyGuard.SafeConfirmation(reply, Goal, null, "en");

        result.ShouldContain(Goal);
        result.ShouldContain("?");
    }

    [TestCase("de", "Möchtest du")]
    [TestCase("en", "Do you want")]
    [TestCase("fr", "Veux-tu")]
    [TestCase("it", "Vuoi")]
    public void SafeConfirmation_DeterministicFallback_LocalizedForCoreLanguages(string language, string expectedLead)
    {
        var result = RecipeReplyGuard.SafeConfirmation("Erledigt.", Goal, null, language);

        result.ShouldStartWith(expectedLead);
        result.ShouldContain(Goal);
    }

    [TestCase("es")]
    [TestCase("zh-CN")]
    [TestCase(null)]
    [TestCase("")]
    public void SafeConfirmation_DeterministicFallback_NonCoreLanguage_UsesEnglishFrame(string? language)
    {
        var result = RecipeReplyGuard.SafeConfirmation("Erledigt.", Goal, null, language);

        result.ShouldStartWith("Do you want");
        result.ShouldContain(Goal);
    }

    [Test]
    public void SafeConfirmation_WithAlternativeGoal_DeterministicFallback_OffersBoth()
    {
        var result = RecipeReplyGuard.SafeConfirmation("Erledigt.", Goal, AlternativeGoal, "de");

        result.ShouldContain(Goal);
        result.ShouldContain(AlternativeGoal);
        result.ShouldContain("?");
    }

    [Test]
    public void SafeAsk_WellFormedQuestion_PassesThroughUntouched()
    {
        var reply = "Ab welchem Datum soll die Gruppe gültig sein?";

        RecipeReplyGuard.SafeAsk(reply, "From which date should the group be valid?").ShouldBe(reply);
    }

    [Test]
    public void SafeAsk_CompletionClaim_ReplacedByAskPrompt()
    {
        const string askPrompt = "From which date should the group be valid?";

        var result = RecipeReplyGuard.SafeAsk("Die Gruppe wurde erstellt.", askPrompt);

        result.ShouldBe(askPrompt);
    }

    [Test]
    public void SafeAsk_ToolMarkupLeak_ReplacedByAskPrompt()
    {
        const string askPrompt = "From which date should the group be valid?";
        var reply = "<function_calls><invoke name=\"create_group\"></invoke></function_calls>";

        var result = RecipeReplyGuard.SafeAsk(reply, askPrompt);

        result.ShouldBe(askPrompt);
    }

    [TestCase(null)]
    [TestCase("")]
    public void SafeAsk_EmptyReply_ReplacedByAskPrompt(string? reply)
    {
        const string askPrompt = "From which date should the group be valid?";

        RecipeReplyGuard.SafeAsk(reply, askPrompt).ShouldBe(askPrompt);
    }

    [TestCase("de")]
    [TestCase("en")]
    [TestCase("fr")]
    [TestCase("it")]
    public void SafeAsk_CompletionClaim_FallsBackToLocalizedQuestion(string language)
    {
        const string askPrompt = "Ask the user from which date the group should be valid.";

        var result = RecipeReplyGuard.SafeAsk("Die Gruppe wurde erstellt.", askPrompt, AskTranslations, language);

        result.ShouldBe(AskTranslations[language]);
    }

    [TestCase("es")]
    [TestCase("zh-CN")]
    [TestCase(null)]
    [TestCase("")]
    public void SafeAsk_NonCoreLanguage_FallsBackToEnglishTranslation(string? language)
    {
        const string askPrompt = "Ask the user from which date the group should be valid.";

        var result = RecipeReplyGuard.SafeAsk("Erledigt.", askPrompt, AskTranslations, language);

        result.ShouldBe(AskTranslations["en"]);
    }

    [Test]
    public void SafeAsk_TranslationsWithoutRequestedLanguage_FallsBackToEnglishEntry()
    {
        var partial = new Dictionary<string, string> { ["en"] = "From when?" };

        var result = RecipeReplyGuard.SafeAsk("Erledigt.", "Ask for the date.", partial, "de");

        result.ShouldBe("From when?");
    }

    [Test]
    public void SafeAsk_NoTranslations_FallsBackToAskPrompt()
    {
        const string askPrompt = "From which date should the group be valid?";

        var result = RecipeReplyGuard.SafeAsk("Erledigt.", askPrompt, null, "de");

        result.ShouldBe(askPrompt);
    }

    [Test]
    public void SafeAsk_WellFormedQuestion_WinsOverLocalizedFallback()
    {
        var reply = "Ab welchem Datum soll die Gruppe gültig sein?";

        RecipeReplyGuard.SafeAsk(reply, "Ask for the date.", AskTranslations, "fr").ShouldBe(reply);
    }

    [TestCase("de")]
    [TestCase("fr")]
    [TestCase("it")]
    public void SafeConfirmation_DeterministicFallback_UsesLocalizedGoal(string language)
    {
        var result = RecipeReplyGuard.SafeConfirmation(
            "Erledigt.", Goal, null, language, GoalTranslations);

        result.ShouldContain(GoalTranslations[language]);
        result.ShouldNotContain(Goal);
    }

    [Test]
    public void SafeConfirmation_WithAlternative_UsesLocalizedGoalsForBoth()
    {
        var result = RecipeReplyGuard.SafeConfirmation(
            "Erledigt.", Goal, AlternativeGoal, "de", GoalTranslations, AlternativeGoalTranslations);

        result.ShouldContain(GoalTranslations["de"]);
        result.ShouldContain(AlternativeGoalTranslations["de"]);
        result.ShouldContain("?");
    }

    [Test]
    public void SafeConfirmation_NonCoreLanguage_UsesEnglishGoalTranslation()
    {
        var translations = new Dictionary<string, string>
        {
            ["de"] = "Eine neue Gruppe erstellen.",
            ["en"] = "Create a brand-new group.",
        };

        var result = RecipeReplyGuard.SafeConfirmation("Erledigt.", Goal, null, "es", translations);

        result.ShouldStartWith("Do you want");
        result.ShouldContain("Create a brand-new group.");
    }

    [Test]
    public void SafeConfirmation_NoTranslations_FallsBackToRawGoal()
    {
        var result = RecipeReplyGuard.SafeConfirmation("Erledigt.", Goal, null, "de", null);

        result.ShouldContain(Goal);
    }
}
