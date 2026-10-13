// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The setup consultation reads free-text answers with the affirmation and decline detectors. A reply made of a word
/// that languages disagree about (Polish "tak" is a yes, Malay "tak" is a no) must come out as the language says in a
/// Polish or Malay UI and as Unknown - the cautious path that re-asks - anywhere else: reading it as a refusal would
/// land on None, which seals a clientless service immediately and irreversibly.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SetupConsultationAnswerClassifierCollisionTests
{
    [OneTimeSetUp]
    public void InstallThePacks() => ShippedConversationSignals.Install();

    [OneTimeTearDown]
    public void UninstallThePacks() => ShippedConversationSignals.Uninstall();

    [TestCase("pl", SetupAttributionAnswer.Customer)]
    [TestCase("ms", SetupAttributionAnswer.None)]
    [TestCase("de", SetupAttributionAnswer.Unknown)]
    [TestCase(null, SetupAttributionAnswer.Unknown)]
    public void Attribution_ASettledTakIsTheLanguagesAnswer_AnUnsettledOneIsUnknown(
        string? language, SetupAttributionAnswer expected)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution("tak", language).ShouldBe(expected);
    }

    [TestCase("pl", SetupOrderSourceAnswer.External)]
    [TestCase("ms", SetupOrderSourceAnswer.Manual)]
    [TestCase("de", SetupOrderSourceAnswer.Unknown)]
    [TestCase(null, SetupOrderSourceAnswer.Unknown)]
    public void OrderSource_ASettledTakIsTheLanguagesAnswer_AnUnsettledOneIsUnknown(
        string? language, SetupOrderSourceAnswer expected)
    {
        SetupConsultationAnswerClassifier.ClassifyOrderSource("tak", language).ShouldBe(expected);
    }

    [TestCase("ms", SetupNextStepChoice.None)]
    [TestCase("de", SetupNextStepChoice.Unknown)]
    [TestCase(null, SetupNextStepChoice.Unknown)]
    public void NextStep_ARefusingTakIsNoneOnlyInALanguageThatReadsItAsANo(
        string? language, SetupNextStepChoice expected)
    {
        SetupConsultationAnswerClassifier.ClassifyNextStep("tak", language).ShouldBe(expected);
    }

    [Test]
    public void AReplyWithoutACollisionWord_IsClassifiedAsBefore_WhateverTheLanguage()
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution("nein", "de").ShouldBe(SetupAttributionAnswer.None);
        SetupConsultationAnswerClassifier.ClassifyAttribution("ja", null).ShouldBe(SetupAttributionAnswer.Customer);
    }
}
