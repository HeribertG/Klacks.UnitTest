// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The detectors against the REAL language packs and the collision table generated from them. Polish "tak" is a yes in
/// the Polish pack and a no in the Malay pack, and the detectors merge both into one vocabulary where the negation
/// wins; the UI language has to settle it: yes in a Polish UI, no in a Malay UI, and neither - Ambiguous - anywhere
/// else, including an unknown language. The second half covers LeadsWithAffirmation, which used to read an imperative
/// or a homograph at the start of a request ("Los empleados nuevos, por favor") as the user accepting an offer.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AffirmationDetectorCollisionTests
{
    [OneTimeSetUp]
    public void InstallThePacks() => ShippedConversationSignals.Install();

    [OneTimeTearDown]
    public void UninstallThePacks() => ShippedConversationSignals.Uninstall();

    [TestCase("tak", "pl", ConfirmationVerdict.Confirmed)]
    [TestCase("Tak.", "pl", ConfirmationVerdict.Confirmed)]
    [TestCase("tak", "pl-PL", ConfirmationVerdict.Confirmed)]
    [TestCase("tak", "PL", ConfirmationVerdict.Confirmed)]
    [TestCase("tak, dobrze", "pl", ConfirmationVerdict.Confirmed)]
    [TestCase("tak", "ms", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak, dobrze", "ms", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak", "de", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", "en", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", "zh-CN", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", "", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", null, ConfirmationVerdict.Ambiguous)]
    [TestCase("tak, dobrze", "de", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak, dobrze", null, ConfirmationVerdict.Ambiguous)]
    [TestCase("tak?", "pl", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak, nie", "pl", ConfirmationVerdict.NotConfirmed)]
    [TestCase("nein", "pl", ConfirmationVerdict.NotConfirmed)]
    public void AssessAffirmation_SettlesTheCollidingTakByTheUiLanguage(
        string message, string? language, ConfirmationVerdict expected)
    {
        AffirmationDetector.AssessAffirmation(message, language).Verdict.ShouldBe(expected, $"'{message}' in '{language}'");
    }

    [TestCase("tak", "pl", ConfirmationVerdict.Confirmed)]
    [TestCase("tak", "ms", ConfirmationVerdict.NotConfirmed)]
    [TestCase("tak", "de", ConfirmationVerdict.Ambiguous)]
    [TestCase("tak", null, ConfirmationVerdict.Ambiguous)]
    [TestCase("tak, wykonaj", "pl", ConfirmationVerdict.Confirmed)]
    [TestCase("tak, wykonaj", "de", ConfirmationVerdict.Ambiguous)]
    public void AssessExplicitConfirmation_NeverConfirmsAnUnsettledCollision(
        string message, string? language, ConfirmationVerdict expected)
    {
        AffirmationDetector.AssessExplicitConfirmation(message, language).Verdict.ShouldBe(expected);
        AffirmationDetector.IsExplicitConfirmation(message, language).ShouldBe(expected == ConfirmationVerdict.Confirmed);
    }

    [Test]
    public void AnAmbiguousVerdict_NamesTheWordAndEveryLanguageThatDisagreesAboutIt()
    {
        var assessment = AffirmationDetector.AssessExplicitConfirmation("tak", "de");

        assessment.IsAmbiguous.ShouldBeTrue();
        assessment.AmbiguousWord.ShouldBe("tak");
        assessment.AmbiguousLanguages.ShouldBe(["ms", "pl"]);
        AffirmationDetector.IsAffirmation("tak", "de").ShouldBeFalse();
        AffirmationDetector.IsAffirmation("tak").ShouldBeFalse();
    }

    [Test]
    public void TheLanguageSettlesOnlyTheCollidingWord_EveryOtherYesAndNoKeepsItsMeaning()
    {
        AffirmationDetector.IsExplicitConfirmation("ja", "pl").ShouldBeTrue();
        AffirmationDetector.IsExplicitConfirmation("dobrze", "ms").ShouldBeTrue();
        AffirmationDetector.IsExplicitConfirmation("tidak", "pl").ShouldBeFalse();
        AffirmationDetector.AssessAffirmation("tidak", "pl").IsAmbiguous.ShouldBeFalse();
    }

    [TestCase("tak", "pl", false)]
    [TestCase("tak, zrób to", "pl", false)]
    [TestCase("tak", "ms", true)]
    [TestCase("tak", "de", false)]
    [TestCase("tak", null, false)]
    [TestCase("tak", "da", false)]
    [TestCase("tak?", "de", false)]
    [TestCase("tak?", "ms", true)]
    public void DeclineDetector_ReadsTakAsARefusalOnlyWhereTheLanguageListsItAsANo(string message, string? language, bool refusal)
    {
        DeclineDetector.IsBareNegation(message, language).ShouldBe(refusal && !message.Contains(','));
        DeclineDetector.LeadsWithNegation(message, language).ShouldBe(refusal);
    }

    [Test]
    public void DeclineDetector_StripsTheNegationLeadOnlyWhereTheWordIsANo()
    {
        DeclineDetector.StripNegationLead("tak, zrób to", "ms").ShouldBe("zrób to");
        DeclineDetector.StripNegationLead("tak, zrób to", "pl").ShouldBe("tak, zrób to");
    }

    [TestCase("Los empleados nuevos, por favor")]
    [TestCase("Los empleados nuevos")]
    [TestCase("Los turnos de Pérez")]
    [TestCase("Mach einen Dienst für Montag")]
    [TestCase("Weiter zum nächsten Mitarbeiter")]
    [TestCase("Go to the schedule")]
    [TestCase("Continue with the next employee in the list")]
    [TestCase("Kör autofyll nu")]
    [TestCase("Prima di tutto crea il turno")]
    [TestCase("Neuen Mitarbeiter, bitte")]
    [TestCase("Si Pérez falta")]
    public void LeadsWithAffirmation_False_ForARequestThatOpensWithAnImperativeOrHomograph(string message)
    {
        AffirmationDetector.LeadsWithAffirmation(message).ShouldBeFalse(message);
    }

    [TestCase("Los geht's")]
    [TestCase("Los!")]
    [TestCase("Los, mach")]
    [TestCase("Los, ausführen")]
    [TestCase("Mach das")]
    [TestCase("Go ahead")]
    [TestCase("Ja, übernimm die Gruppen so")]
    [TestCase("Yes, apply it")]
    [TestCase("Sí, borra el turno")]
    [TestCase("Si, procedi")]
    [TestCase("Kör, autofyll")]
    [TestCase("ya, lanjutkan")]
    [TestCase("はい、全員")]
    public void LeadsWithAffirmation_True_ForAnAcceptanceThatOpensWithTheYesOrIsAShortReply(string message)
    {
        AffirmationDetector.LeadsWithAffirmation(message).ShouldBeTrue(message);
    }

    [TestCase("Sí hazlo", "es")]
    [TestCase("Sí hazlo por favor", "es")]
    [TestCase("Sim pode fazer", "pt")]
    [TestCase("Sim pode fazer isso agora", "pt")]
    [TestCase("ya lanjutkan", "ms")]
    [TestCase("ya teruskan sekarang juga", "ms")]
    [TestCase("ya lanjutkan", "id")]
    [TestCase("ya lanjutkan sekarang juga", "id")]
    [TestCase("Tak dobrze", "pl")]
    [TestCase("Tak dobrze zrób to teraz", "pl")]
    [TestCase("Claro sí adelante con todo", "es")]
    [TestCase("Claro please do it now", "es")]
    [TestCase("Sí bitte", "es")]
    [TestCase("Mach bitte", null)]
    public void LeadsWithAffirmation_True_ForASpokenAcceptanceWithoutCommas(string message, string? language)
    {
        AffirmationDetector.LeadsWithAffirmation(message, language).ShouldBeTrue($"'{message}' in '{language}'");
    }

    [TestCase("Sí hazlo", "es")]
    [TestCase("Sí hazlo por favor", "es")]
    [TestCase("Sim pode fazer", "pt")]
    [TestCase("Sim pode fazer isso agora", "pt")]
    [TestCase("ya teruskan sekarang juga", "ms")]
    [TestCase("ya lanjutkan sekarang juga", "id")]
    [TestCase("Tak dobrze zrób to teraz", "pl")]
    public void IsExplicitConfirmation_True_ForASpokenYesWithoutCommas(string message, string? language)
    {
        AffirmationDetector.IsExplicitConfirmation(message, language).ShouldBeTrue($"'{message}' in '{language}'");
    }

    [TestCase("Los empleados nuevos", "es")]
    [TestCase("Si Pérez falta", "es")]
    [TestCase("Sí Pérez falta", "es")]
    [TestCase("Claro Pérez falta", "es")]
    [TestCase("Mach einen Dienst für Montag", "de")]
    [TestCase("Mach das Licht an", "de")]
    [TestCase("Go for Monday", "en")]
    [TestCase("Weiter zum nächsten Mitarbeiter", "de")]
    [TestCase("Kör autofyll nu", "sv")]
    [TestCase("Tak Pérez falta", "pl")]
    [TestCase("Sim Pérez trabalha amanhã", "pt")]
    [TestCase("Mach bitte einen Dienst für Montag", "de")]
    [TestCase("Weiter bitte zum nächsten Mitarbeiter", "de")]
    [TestCase("Los bitte die Gruppe Bern anlegen", "de")]
    [TestCase("Go please add a shift for Monday", "en")]
    [TestCase("Go do the planning for Monday", "en")]
    [TestCase("Mach weiter mit der Planung", "de")]
    [TestCase("Claro please add a shift for Monday", "es")]
    [TestCase("Go to the schedule", null)]
    public void LeadsWithAffirmationAndIsExplicitConfirmation_False_WhenTheNextWordAnswersNothing(string message, string? language)
    {
        AffirmationDetector.LeadsWithAffirmation(message, language).ShouldBeFalse($"LeadsWith '{message}' in '{language}'");
        AffirmationDetector.IsExplicitConfirmation(message, language).ShouldBeFalse($"Explicit '{message}' in '{language}'");
    }

    [Test]
    public void LeadsWithAffirmation_ASettledTak_OpensAPolishAcceptanceAndNothingElse()
    {
        AffirmationDetector.LeadsWithAffirmation("Tak, dobrze", "pl").ShouldBeTrue();
        AffirmationDetector.LeadsWithAffirmation("Tak, dobrze", "ms").ShouldBeFalse();
        AffirmationDetector.LeadsWithAffirmation("Tak, dobrze", "de").ShouldBeFalse();
        AffirmationDetector.LeadsWithAffirmation("Tak, dobrze").ShouldBeFalse();
    }
}
