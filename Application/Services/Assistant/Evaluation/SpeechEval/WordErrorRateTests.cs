// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.SpeechEval;

using Klacks.Api.Application.Services.Assistant.Evaluation.SpeechEval;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class WordErrorRateTests
{
    private const double Tolerance = 0.0001;

    [Test]
    public void Compute_IdenticalTexts_ReturnsZero()
    {
        var wer = WordErrorRate.Compute(
            "Bitte trage die Frühschicht für Montag ein",
            "Bitte trage die Frühschicht für Montag ein");

        wer.ShouldBe(0.0, Tolerance);
    }

    [Test]
    public void Compute_CompletelyDifferentSameLength_ReturnsOne()
    {
        var wer = WordErrorRate.Compute("alpha beta", "gamma delta");

        wer.ShouldBe(1.0, Tolerance);
    }

    [Test]
    public void Compute_OneSubstitutionAndOneInsertion_ReturnsTwoOverFour()
    {
        var wer = WordErrorRate.Compute("eins zwei drei vier", "eins falsch drei vier extra");

        wer.ShouldBe(0.5, Tolerance);
    }

    [Test]
    public void Compute_OneDeletion_ReturnsOneOverFour()
    {
        var wer = WordErrorRate.Compute("eins zwei drei vier", "eins zwei vier");

        wer.ShouldBe(0.25, Tolerance);
    }

    [Test]
    public void Compute_EmptyHypothesis_ReturnsOne()
    {
        var wer = WordErrorRate.Compute("eins zwei drei", string.Empty);

        wer.ShouldBe(1.0, Tolerance);
    }

    [Test]
    public void Compute_BothEmpty_ReturnsZero()
    {
        var wer = WordErrorRate.Compute(string.Empty, string.Empty);

        wer.ShouldBe(0.0, Tolerance);
    }

    [Test]
    public void Compute_EmptyReferenceNonEmptyHypothesis_ReturnsOne()
    {
        var wer = WordErrorRate.Compute(string.Empty, "unerwarteter text");

        wer.ShouldBe(1.0, Tolerance);
    }

    [Test]
    public void Compute_ManyInsertions_CanExceedOne()
    {
        var wer = WordErrorRate.Compute("eins", "zwei drei vier");

        wer.ShouldBe(3.0, Tolerance);
    }

    [Test]
    public void Compute_UmlautCaseAndPunctuationDifferences_ReturnsZero()
    {
        var wer = WordErrorRate.Compute(
            "Frau Müller, kommt am 3. August!",
            "frau muller kommt am 3 august");

        wer.ShouldBe(0.0, Tolerance);
    }

    [Test]
    public void ComputeNameAccuracy_AllNamesFound_ReturnsOne()
    {
        var accuracy = WordErrorRate.ComputeNameAccuracy(
            "Hans-Peter Brönnimann übernimmt die Schicht von Annelies Grüter",
            ["Brönnimann", "Grüter"]);

        accuracy.ShouldBe(1.0, Tolerance);
    }

    [Test]
    public void ComputeNameAccuracy_OneOfTwoNamesFound_ReturnsHalf()
    {
        var accuracy = WordErrorRate.ComputeNameAccuracy(
            "Hans-Peter Brönnimann übernimmt die Schicht",
            ["Brönnimann", "Grüter"]);

        accuracy.ShouldBe(0.5, Tolerance);
    }

    [Test]
    public void ComputeNameAccuracy_AccentInsensitiveMatch_ReturnsOne()
    {
        var accuracy = WordErrorRate.ComputeNameAccuracy(
            "bitte informiere frau muller und celine fassler",
            ["Müller", "Fässler"]);

        accuracy.ShouldBe(1.0, Tolerance);
    }

    [Test]
    public void ComputeNameAccuracy_NoExpectedNames_ReturnsOne()
    {
        var accuracy = WordErrorRate.ComputeNameAccuracy("irgendein transkript", []);

        accuracy.ShouldBe(1.0, Tolerance);
    }

    [Test]
    public void ComputeNameAccuracy_NameMissing_ReturnsZero()
    {
        var accuracy = WordErrorRate.ComputeNameAccuracy("die schicht ist offen", ["Nussbaumer"]);

        accuracy.ShouldBe(0.0, Tolerance);
    }

    [Test]
    public void ComputeComposite_PerfectTranscript_ReturnsOne()
    {
        WordErrorRate.ComputeComposite(0.0, 1.0).ShouldBe(1.0, Tolerance);
    }

    [Test]
    public void ComputeComposite_WorstTranscript_ReturnsZero()
    {
        WordErrorRate.ComputeComposite(1.0, 0.0).ShouldBe(0.0, Tolerance);
    }

    [Test]
    public void ComputeComposite_HalfWerHalfNames_ReturnsHalf()
    {
        WordErrorRate.ComputeComposite(0.5, 0.5).ShouldBe(0.5, Tolerance);
    }

    [Test]
    public void ComputeComposite_WerAboveOne_IsClampedToOne()
    {
        WordErrorRate.ComputeComposite(3.0, 1.0).ShouldBe(0.3, Tolerance);
    }
}
