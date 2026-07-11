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
}
