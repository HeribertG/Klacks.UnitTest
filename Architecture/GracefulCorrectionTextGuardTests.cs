// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the two text families of the correction path. The note templates must keep every placeholder
/// their format call fills - string.Format ignores a surplus argument silently, so a lost placeholder
/// drops a value without any error - and the clauses rule 1 depends on. The clarification catalogue must
/// cover every core language with the same placeholders, and an installed language must never resolve to
/// English.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class GracefulCorrectionTextGuardTests
{
    private const string LanguagePlaceholder = "{4}";
    private const string UndoLanguagePlaceholder = "{2}";
    private const string ChineseSimplified = "zh-CN";

    [TearDown]
    public void ResetConfiguredTexts() => GracefulCorrectionTexts.Reset();

    [TestCase(nameof(GracefulCorrectionNotes.CorrectionContextTemplate), 5)]
    [TestCase(nameof(GracefulCorrectionNotes.OpeningSentenceTemplate), 1)]
    [TestCase(nameof(GracefulCorrectionNotes.NamedLanguageTemplate), 1)]
    [TestCase(nameof(GracefulCorrectionNotes.UndoOfferTemplate), 3)]
    public void EveryNoteTemplate_CarriesAllItsPlaceholders(string constantName, int placeholderCount)
    {
        var template = (string)typeof(GracefulCorrectionNotes).GetField(constantName)!.GetRawConstantValue()!;

        for (var index = 0; index < placeholderCount; index++)
        {
            template.ShouldContain(
                "{" + index + "}",
                customMessage:
                    $"{constantName} is formatted with {placeholderCount} argument(s) but has no {{{index}}}");
        }
    }

    [Test]
    public void CorrectionNoteTemplate_DemandsTheVisibleConfirmationInAnExplicitLanguage()
    {
        var template = GracefulCorrectionNotes.CorrectionContextTemplate;

        template.ShouldContain("MUST open your answer");
        template.ShouldContain("Answer in " + LanguagePlaceholder);
        template.ShouldContain("translated into " + LanguagePlaceholder);
        template.ShouldContain("never");
    }

    // The language slot is a whole phrase, not a bare tag: it carries its own quotes for a known tag and
    // none at all for the "same language as the user's message" fallback, so the template must not add a
    // second pair around it.
    [Test]
    public void CorrectionNoteTemplate_DoesNotQuoteTheLanguagePhraseItself()
    {
        GracefulCorrectionNotes.CorrectionContextTemplate.ShouldNotContain("'" + LanguagePlaceholder + "'");
        GracefulCorrectionNotes.LanguageOfTheUserMessage.ShouldNotStartWith("'");
        GracefulCorrectionNotes.LanguageOfTheUserMessage.ShouldNotEndWith("'");
        GracefulCorrectionNotes.NamedLanguageTemplate.ShouldContain("'{0}'");
    }

    [Test]
    public void CorrectionNoteTemplate_PermitsReusingTheExcludedSkillWithCorrectedArguments()
    {
        GracefulCorrectionNotes.CorrectionContextTemplate.ShouldContain("only changes an argument");
    }

    [Test]
    public void UndoOfferTemplate_AsksForExactlyOneSentenceInAnExplicitLanguage()
    {
        GracefulCorrectionNotes.UndoOfferTemplate.ShouldContain("exactly ONE short");
        GracefulCorrectionNotes.UndoOfferTemplate.ShouldContain("in " + UndoLanguagePlaceholder);
        GracefulCorrectionNotes.UndoOfferTemplate.ShouldNotContain("'" + UndoLanguagePlaceholder + "'");
        GracefulCorrectionNotes.UndoOfferTemplate.ShouldContain("never as a separate");
    }

    [Test]
    public void ClarificationCatalogue_CoversEveryCoreLanguage_WithEveryPlaceholder()
    {
        foreach (var key in GracefulCorrectionTexts.Keys)
        {
            var variants = GracefulCorrectionTexts.VariantsOf(key);
            foreach (var language in GracefulCorrectionTexts.CoreLanguages)
            {
                variants.ShouldContainKey(language);
                foreach (var placeholder in GracefulCorrectionTexts.RequiredPlaceholders)
                {
                    variants[language].ShouldContain(placeholder, customMessage: $"{key}/{language}");
                }
            }
        }
    }

    [Test]
    public void UnknownLanguage_FallsBackToEnglish()
    {
        GracefulCorrectionTexts.TryGetText(
            GracefulCorrectionTexts.ClarificationQuestion, "xx-XX", out var text).ShouldBeTrue();

        text.ShouldBe(GracefulCorrectionTexts.VariantsOf(
            GracefulCorrectionTexts.ClarificationQuestion)[LanguageConfig.DefaultLanguageFallback]);
    }

    [Test]
    public void InstalledPluginLanguage_NeverFallsBackToEnglish()
    {
        GracefulCorrectionTexts.Configure(ChineseSimplified, new Dictionary<string, string>
        {
            [GracefulCorrectionTexts.ClarificationQuestion] = "明白了——不是{previousAction}。{optionA}还是{optionB}？"
        });

        GracefulCorrectionTexts.TryGetText(
            GracefulCorrectionTexts.ClarificationQuestion, ChineseSimplified, out var text).ShouldBeTrue();

        text.ShouldStartWith("明白了");
    }

    [Test]
    public void InstalledLanguageWithAMissingKey_AsksNothingRatherThanAskingInEnglish()
    {
        GracefulCorrectionTexts.Configure(
            ChineseSimplified, new Dictionary<string, string> { ["some.other.key"] = "x" });

        GracefulCorrectionTexts.TryGetText(
            GracefulCorrectionTexts.ClarificationQuestion, ChineseSimplified, out _).ShouldBeFalse();
    }
}
