// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the lexical anchor the semantic recipe fallback requires: allOf[0] is the verb group,
/// every later condition is an anchor, and a candidate stands when the message hits at least one of them,
/// whatever the recipe size. Locks the counting (verb group excluded), the single required anchor, the
/// exemptions (no non-verb condition, non-core language) and the language binding of locale-bound
/// conditions.
/// </summary>

using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTriggerMatcherAnchorTests
{
    private const string German = "de";
    private const string Italian = "it";
    private const string Spanish = "es";

    private static RecipeCondition Verb() => new() { AnyWordStart = ["hinzufüg", "add"] };

    private static RecipeCondition Group() => new() { AnySubstring = ["gruppe", "team"] };

    private static RecipeCondition Extern() => new() { AnySubstring = ["extern"] };

    private static RecipeCondition Nearest() => new() { AnySubstring = ["nächst", "nearest"] };

    private static RecipeCondition BulkMarker() => new()
    {
        AnyWordStartByLocale = new Dictionary<string, List<string>> { ["de"] = ["alle"] }
    };

    private static RecipeTrigger Trigger(params RecipeCondition[] conditions) => new() { AllOf = [.. conditions] };

    [Test]
    public void MinRequiredAnchors_IsOneRegardlessOfRecipeSize()
    {
        RecipeTriggerMatcher.MinRequiredAnchors.ShouldBe(1);
    }

    [Test]
    public void CountAnchors_DoesNotCountTheVerbGroup()
    {
        var trigger = Trigger(Verb(), Group());

        RecipeTriggerMatcher.CountAnchors(trigger, "Füge hinzu").ShouldBe(0);
    }

    [Test]
    public void CountAnchors_CountsEveryHitNonVerbCondition()
    {
        var trigger = Trigger(Verb(), Group(), Extern(), Nearest());

        RecipeTriggerMatcher.CountAnchors(trigger, "Externe Mitarbeiter, Gruppe egal").ShouldBe(2);
    }

    [Test]
    public void CountAnchors_MessageWithoutAnyAnchor_IsZero()
    {
        var trigger = Trigger(Verb(), Group(), Extern());

        RecipeTriggerMatcher.CountAnchors(trigger, "Lies bitte meine Notizen").ShouldBe(0);
    }

    [Test]
    public void CountAnchors_NullTriggerOrBlankMessage_IsZero()
    {
        RecipeTriggerMatcher.CountAnchors(null, "Gruppe").ShouldBe(0);
        RecipeTriggerMatcher.CountAnchors(Trigger(Verb(), Group()), "  ").ShouldBe(0);
        RecipeTriggerMatcher.CountAnchors(Trigger(Verb(), Group()), null).ShouldBe(0);
    }

    [Test]
    public void CountAnchors_LocaleBoundCondition_OnlyCountsForItsLanguage()
    {
        var trigger = Trigger(Verb(), BulkMarker());

        RecipeTriggerMatcher.CountAnchors(trigger, "Alle bitte", language: German).ShouldBe(1);
        RecipeTriggerMatcher.CountAnchors(trigger, "Alle bitte", language: Italian).ShouldBe(0);
        RecipeTriggerMatcher.CountAnchors(trigger, "Alle bitte").ShouldBe(0);
    }

    [Test]
    public void HasSemanticAnchor_NullTrigger_IsUnrestricted()
    {
        RecipeTriggerMatcher.HasSemanticAnchor(null, "irgendwas").ShouldBeTrue();
    }

    [Test]
    public void HasSemanticAnchor_EmptyAllOf_IsUnrestricted()
    {
        RecipeTriggerMatcher.HasSemanticAnchor(Trigger(), "irgendwas").ShouldBeTrue();
    }

    [Test]
    public void HasSemanticAnchor_SingleConditionRecipe_IsUnrestricted()
    {
        var phraseOnly = Trigger(new RecipeCondition { AnySubstring = ["erste schritte"] });

        RecipeTriggerMatcher.HasSemanticAnchor(phraseOnly, "Womit soll ich beginnen?", language: German).ShouldBeTrue();
    }

    [Test]
    public void HasSemanticAnchor_TwoConditions_NeedsTheSubject()
    {
        var trigger = Trigger(Verb(), Group());

        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Ich brauche ein Team", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Lies meine Notizen", language: German).ShouldBeFalse();
    }

    [Test]
    public void HasSemanticAnchor_ThreeConditions_OneSubjectConditionSuffices()
    {
        var trigger = Trigger(Verb(), Group(), Extern());

        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Externe Leute", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Gruppe", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Externe Leute fürs Team", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Lies meine Notizen", language: German).ShouldBeFalse();
    }

    [Test]
    public void HasSemanticAnchor_FourConditions_OneOfThreeSubjectConditionsSuffices()
    {
        var trigger = Trigger(Verb(), Group(), Extern(), Nearest());

        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Externe fürs Team", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Externe Leute", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Zum Nächsten", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Lies meine Notizen", language: German).ShouldBeFalse();
    }

    [Test]
    public void HasSemanticAnchor_LanguageOutsideTheCoreSet_IsUnrestricted()
    {
        var trigger = Trigger(Verb(), Group());

        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Lee mis notas", language: Spanish).ShouldBeTrue();
    }

    [Test]
    public void HasSemanticAnchor_CoreLanguageCode_IsMatchedCaseInsensitively()
    {
        var trigger = Trigger(Verb(), Group());

        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Lies meine Notizen", language: "DE").ShouldBeFalse();
    }

    [Test]
    public void HasSemanticAnchor_UnknownLanguage_IsGatedLikeACoreLanguage()
    {
        var trigger = Trigger(Verb(), Group());

        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Lies meine Notizen").ShouldBeFalse();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Lies meine Notizen", language: "").ShouldBeFalse();
    }

    [Test]
    public void HasSemanticAnchor_LocaleBoundSubject_IsBoundToTheDetectedLanguage()
    {
        var trigger = Trigger(Verb(), BulkMarker());

        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Alle bitte", language: German).ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, "Alle bitte", language: Italian).ShouldBeFalse();
    }
}
