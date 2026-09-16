// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DeclineDetector — verifies that replies opening with a refusal ("nein",
/// "no, not now") are detected as leading negations, while genuine requests that merely contain
/// a negation later in the sentence are not.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class DeclineDetectorTests
{
    [TearDown]
    public void ResetPluginEntries()
    {
        DeclineDetector.Reset();
    }

    [TestCase("Nein")]
    [TestCase("Nein, nein, nein, nein, nein, nein.")]
    [TestCase("Nein, im Moment will ich nicht zuhören.")]
    [TestCase("Nein, im Moment will ich nicht zuhüssen.")]
    [TestCase("Nein danke.")]
    [TestCase("Nö, lass mal.")]
    [TestCase("Nee, jetzt nicht.")]
    [TestCase("Nicht jetzt.")]
    [TestCase("No, not now.")]
    [TestCase("Nope.")]
    [TestCase("Non merci.")]
    [TestCase("Danke, nein.")]
    public void LeadsWithNegation_DeclineReplies_ReturnsTrue(string message)
    {
        DeclineDetector.LeadsWithNegation(message).ShouldBeTrue();
    }

    [TestCase("Kannst du nicht die Adressen anzeigen?")]
    [TestCase("Ich will einen neuen Mitarbeiter anlegen.")]
    [TestCase("Zeig mir bitte die Einstellungen.")]
    [TestCase("Erstelle eine neue Gruppe.")]
    [TestCase("Welche Gruppen gibt es?")]
    [TestCase("Der Kunde hat keine Adresse hinterlegt, was tun?")]
    public void LeadsWithNegation_GenuineRequests_ReturnsFalse(string message)
    {
        DeclineDetector.LeadsWithNegation(message).ShouldBeFalse();
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void LeadsWithNegation_EmptyMessages_ReturnsFalse(string? message)
    {
        DeclineDetector.LeadsWithNegation(message).ShouldBeFalse();
    }

    [Test]
    public void LeadsWithNegation_PluginDeclinePhraseAsPrefix_ReturnsTrue()
    {
        DeclineDetector.Configure([], ["ahora no"]);

        DeclineDetector.LeadsWithNegation("Ahora no, gracias.").ShouldBeTrue();
    }

    [Test]
    public void LeadsWithNegation_PluginNegationToken_ReturnsTrue()
    {
        DeclineDetector.Configure(["nie"], []);

        DeclineDetector.LeadsWithNegation("Nie, dziękuję.").ShouldBeTrue();
    }

    [TestCase("Nein")]
    [TestCase("nein.")]
    [TestCase("Nein, nein!")]
    [TestCase("No")]
    [TestCase("Non")]
    [TestCase("Nö")]
    public void IsBareNegation_PureRefusals_ReturnsTrue(string message)
    {
        DeclineDetector.IsBareNegation(message).ShouldBeTrue();
    }

    [TestCase("no thanks")]
    [TestCase("Nein, zeig mir stattdessen die Kunden")]
    [TestCase("Nicht jetzt, sondern morgen bitte")]
    [TestCase("Zeig mir die offenen Schichten")]
    [TestCase("")]
    [TestCase(null)]
    public void IsBareNegation_AnythingCarryingContent_ReturnsFalse(string? message)
    {
        DeclineDetector.IsBareNegation(message).ShouldBeFalse();
    }

    [Test]
    public void IsBareNegation_SingleTokenPluginNegation_ReturnsTrue()
    {
        DeclineDetector.Configure(["nie"], []);

        DeclineDetector.IsBareNegation("Nie.").ShouldBeTrue();
    }

    [Test]
    public void IsBareNegation_MultiWordPluginNegation_ReturnsFalse()
    {
        DeclineDetector.Configure(["ahora no"], []);

        DeclineDetector.IsBareNegation("Ahora no").ShouldBeFalse();
    }

    [Test]
    public void Reset_DiscardsEveryConfiguredPluginEntry()
    {
        DeclineDetector.Configure(["nie"], ["mas tarde"]);

        DeclineDetector.Reset();

        DeclineDetector.LeadsWithNegation("Nie, dziękuję.").ShouldBeFalse();
        DeclineDetector.LeadsWithNegation("Mas tarde, gracias.").ShouldBeFalse();
        DeclineDetector.IsBareNegation("Nie.").ShouldBeFalse();
    }

    [Test]
    public void Reset_KeepsTheCoreLanguageTokens()
    {
        DeclineDetector.Configure(["nie"], []);

        DeclineDetector.Reset();

        DeclineDetector.LeadsWithNegation("Nein danke.").ShouldBeTrue();
        DeclineDetector.IsBareNegation("Nein").ShouldBeTrue();
    }

    [Test]
    public void StripNegationLead_LeadingNegation_RemovesTokenAndSeparator()
    {
        DeclineDetector.StripNegationLead("Nein, wie finde ich das heraus?").ShouldBe("wie finde ich das heraus?");
    }

    [Test]
    public void StripNegationLead_NoLeadingNegation_ReturnsMessageUnchanged()
    {
        DeclineDetector.StripNegationLead("Zeig mir die offenen Schichten").ShouldBe("Zeig mir die offenen Schichten");
    }
}
