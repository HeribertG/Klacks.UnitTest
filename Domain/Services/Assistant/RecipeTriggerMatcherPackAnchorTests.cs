// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the language-pack side of the semantic recipe anchor (rule R3): a candidate stands when
/// the core trigger names the subject OR any installed pack has an anchor term in the message. The request
/// language is the UI language, so the pack side is the union of every installed language and never only
/// the UI language. Locks the match mode per anchor language (substring for ja/zh/th/ar/he and the compounding
/// da/fi/sv, word start for the explicit rest), the union, the fail-open exemption for a non-core UI language without anchors, and that the
/// single-condition recipes stay ungated.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTriggerMatcherPackAnchorTests
{
    private const string German = "de";
    private const string Spanish = "es";
    private const string Polish = "pl";
    private const string Japanese = "ja";
    private const string Korean = "ko";
    private const string Danish = "da";
    private const string Finnish = "fi";
    private const string Swedish = "sv";

    private static RecipeCondition Verb() => new() { AnyWordStart = ["hinzufüg", "add"] };

    private static RecipeCondition Group() => new() { AnySubstring = ["gruppe", "team"] };

    private static RecipeTrigger TwoConditionTrigger() => new() { AllOf = [Verb(), Group()] };

    private static Dictionary<string, List<string>> Anchors(params (string Language, string[] Terms)[] entries) =>
        entries.ToDictionary(e => e.Language, e => e.Terms.ToList());

    [Test]
    public void CoreHit_PassesWithoutAnyPackAnchor()
    {
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Ich brauche ein Team", language: German,
                packAnchors: Anchors((Spanish, ["grupo"])))
            .ShouldBeTrue();
    }

    [Test]
    public void SpanishMessageUnderGermanUi_PassesThroughTheSpanishPack()
    {
        const string message = "Pon a la gente en el grupo, por favor";

        RecipeTriggerMatcher.CountAnchors(TwoConditionTrigger(), message, language: German).ShouldBe(0);
        RecipeTriggerMatcher.HasSemanticAnchor(TwoConditionTrigger(), message, language: German).ShouldBeFalse();
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), message, language: German, packAnchors: Anchors((Spanish, ["grupo"])))
            .ShouldBeTrue();
    }

    [Test]
    public void PackAnchor_MatchesCaseInsensitively()
    {
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "GRUPO NUEVO", language: German, packAnchors: Anchors((Spanish, ["grupo"])))
            .ShouldBeTrue();
    }

    [Test]
    public void WordStartLanguage_DoesNotMatchInsideAWord()
    {
        var anchors = Anchors((Spanish, ["grupo"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee el subgrupo de notas", language: German, packAnchors: anchors)
            .ShouldBeFalse();
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee los grupos de notas", language: German, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [Test]
    public void SubstringLanguage_MatchesInsideAWord()
    {
        var anchors = Anchors((Japanese, ["グループ"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "サブグループに追加して", language: German, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [Test]
    public void MatchMode_FollowsTheAnchorLanguage_NotTheUiLanguage()
    {
        var anchors = Anchors((Spanish, ["grupo"]), (Japanese, ["メモ帳"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee el subgrupo de notas", language: Japanese, packAnchors: anchors)
            .ShouldBeFalse();
    }

    [Test]
    public void KoreanIsAWordStartLanguage()
    {
        RecipeAnchorMatchLanguages.SubstringMatchLanguages.ShouldNotContain(Korean);
        RecipeAnchorMatchLanguages.WordStartMatchLanguages.ShouldContain(Korean);
        var anchors = Anchors((Korean, ["그룹"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "그룹에 추가", language: German, packAnchors: anchors)
            .ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "하위그룹 메모", language: German, packAnchors: anchors)
            .ShouldBeFalse();
    }

    [TestCase("ja")]
    [TestCase("zh-CN")]
    [TestCase("zh-cn")]
    [TestCase("zh-TW")]
    [TestCase("th")]
    [TestCase("ar")]
    [TestCase("he")]
    [TestCase("da")]
    [TestCase("fi")]
    [TestCase("sv")]
    [TestCase("SV")]
    public void SubstringLanguages_AreTheUnsegmentedGluedAndCompoundingLanguages(string language)
    {
        RecipeAnchorMatchLanguages.SubstringMatchLanguages.ShouldContain(language);
        RecipeAnchorMatchLanguages.WordStartMatchLanguages.ShouldNotContain(language);
    }

    [TestCase("cs")]
    [TestCase("el")]
    [TestCase("es")]
    [TestCase("id")]
    [TestCase("ko")]
    [TestCase("ms")]
    [TestCase("nb")]
    [TestCase("nl")]
    [TestCase("pl")]
    [TestCase("pt")]
    [TestCase("ro")]
    [TestCase("vi")]
    public void WordStartLanguages_AreNamedExplicitly(string language)
    {
        RecipeAnchorMatchLanguages.WordStartMatchLanguages.ShouldContain(language);
        RecipeAnchorMatchLanguages.SubstringMatchLanguages.ShouldNotContain(language);
    }

    [TestCase(Danish, "adgang", "Opret systemadgang til den nye kollega")]
    [TestCase(Finnish, "kalenteri", "Päivitä pyhäkalenteri ensi vuodelle")]
    [TestCase(Swedish, "order", "Skapa en passorder för nästa vecka")]
    public void CompoundingLanguage_MatchesTheHeadNounAtTheEndOfACompound(string language, string term, string message)
    {
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), message, language: German, packAnchors: Anchors((language, [term])))
            .ShouldBeTrue();
    }

    [Test]
    public void UnclassifiedLanguage_FallsBackToWordStart()
    {
        const string unclassified = "xx";
        RecipeAnchorMatchLanguages.SubstringMatchLanguages.ShouldNotContain(unclassified);
        RecipeAnchorMatchLanguages.WordStartMatchLanguages.ShouldNotContain(unclassified);
        var anchors = Anchors((unclassified, ["grupo"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee el subgrupo de notas", language: German, packAnchors: anchors)
            .ShouldBeFalse();
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee los grupos de notas", language: German, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [Test]
    public void Union_UsesEveryInstalledLanguage_IndependentOfTheUiLanguage()
    {
        var anchors = Anchors((Spanish, ["grupo"]), (Polish, ["grup"]), (Japanese, ["グループ"]));
        const string polishMessage = "Przenieś pracownika do grupy";

        RecipeTriggerMatcher.HasSemanticAnchor(TwoConditionTrigger(), polishMessage, language: German, packAnchors: anchors)
            .ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(TwoConditionTrigger(), polishMessage, language: Spanish, packAnchors: anchors)
            .ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(TwoConditionTrigger(), polishMessage, language: Japanese, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [Test]
    public void CoreUi_MessageWithoutAnyAnchor_IsRejectedEvenWithPacks()
    {
        var anchors = Anchors((Spanish, ["grupo"]), (Japanese, ["グループ"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lies meine Notizen", language: German, packAnchors: anchors)
            .ShouldBeFalse();
    }

    [Test]
    public void NonCoreUi_WithoutAnchorsForThatLanguage_StaysExempt()
    {
        var anchors = Anchors((Polish, ["grup"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee mis notas", language: Spanish, packAnchors: anchors)
            .ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(TwoConditionTrigger(), "Lee mis notas", language: Spanish)
            .ShouldBeTrue();
    }

    [Test]
    public void NonCoreUi_WithAnEmptyListForThatLanguage_StaysExempt()
    {
        var anchors = Anchors((Spanish, []));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee mis notas", language: Spanish, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [TestCase("es")]
    [TestCase("ES")]
    public void NonCoreUi_WithAnchorsForThatLanguage_IsEvaluated(string uiLanguage)
    {
        var anchors = Anchors((Spanish, ["grupo"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lee mis notas", language: uiLanguage, packAnchors: anchors)
            .ShouldBeFalse();
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Nuevo grupo", language: uiLanguage, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [Test]
    public void NonCoreUi_WithAnchors_StillAcceptsACoreHit()
    {
        var anchors = Anchors((Spanish, ["grupo"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Das Team, bitte", language: Spanish, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [Test]
    public void SingleConditionRecipe_StaysUngatedWhateverThePacks()
    {
        var phraseOnly = new RecipeTrigger { AllOf = [new RecipeCondition { AnySubstring = ["erste schritte"] }] };
        var anchors = Anchors((Spanish, ["primeros pasos"]));

        RecipeTriggerMatcher.HasSemanticAnchor(phraseOnly, "Lee mis notas", language: Spanish, packAnchors: anchors)
            .ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(phraseOnly, "Lies meine Notizen", language: German, packAnchors: anchors)
            .ShouldBeTrue();
    }

    [Test]
    public void BlankAndWhitespaceTerms_AreIgnored()
    {
        var anchors = Anchors((Spanish, ["", "  "]), (Japanese, [""]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lies meine Notizen", language: German, packAnchors: anchors)
            .ShouldBeFalse();
    }

    [Test]
    public void RegexMetacharactersInATerm_AreMatchedLiterally()
    {
        var anchors = Anchors((Spanish, ["c++ (equipo)"]));

        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "Lies c meine Notizen", language: German, packAnchors: anchors)
            .ShouldBeFalse();
        RecipeTriggerMatcher.HasSemanticAnchor(
                TwoConditionTrigger(), "usa c++ (equipo) ya", language: German, packAnchors: anchors)
            .ShouldBeTrue();
    }
}
