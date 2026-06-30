// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NavigationIntentDetector — verifies German and English navigation
/// phrases are detected, info-questions are suppressed, and non-navigation messages return false.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class NavigationIntentDetectorTests
{
    [TestCase("Ich möchte zu den Macros gehen")]
    [TestCase("Gehe zu den Einstellungen")]
    [TestCase("Geh zur Profilseite")]
    [TestCase("Bitte scrolle bis zu Macros")]
    [TestCase("Scrolle zu den LLM-Modellen")]
    [TestCase("Navigiere mich zu den Einstellungen")]
    [TestCase("Navigier zu den Gruppen")]
    [TestCase("Öffne die Einstellungen")]
    [TestCase("Öffne das Profil")]
    [TestCase("Wechsle zur Einstellungsseite")]
    [TestCase("Bring mich zur Settings-Seite")]
    [TestCase("Navigate to the settings")]
    [TestCase("Go to the profile page")]
    public void IsNavigationIntent_ReturnsTrue_ForNavigationPhrases(string message)
    {
        var result = NavigationIntentDetector.IsNavigationIntent(message);

        Assert.That(result, Is.True, $"Expected true for: {message}");
    }

    [TestCase("Wie komme ich zu den Macros?")]
    [TestCase("Wie navigiere ich zu den Einstellungen?")]
    [TestCase("Wie öffne ich das Profil?")]
    [TestCase("Was sind die Einstellungen?")]
    [TestCase("Wo finde ich die Gruppen?")]
    [TestCase("How do I navigate to settings?")]
    [TestCase("What is the profile page?")]
    public void IsNavigationIntent_ReturnsFalse_ForInfoQuestions(string message)
    {
        var result = NavigationIntentDetector.IsNavigationIntent(message);

        Assert.That(result, Is.False, $"Expected false for: {message}");
    }

    [TestCase("Erstelle einen neuen Mitarbeiter")]
    [TestCase("Lösche die Gruppe")]
    [TestCase("Zeig mir alle Kunden")]
    [TestCase("Wie viele Mitarbeiter gibt es?")]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsNavigationIntent_ReturnsFalse_ForNonNavigationMessages(string? message)
    {
        var result = NavigationIntentDetector.IsNavigationIntent(message);

        Assert.That(result, Is.False, $"Expected false for: '{message}'");
    }

    [TestCase("Gehe ich richtig vor?")]
    public void IsNavigationIntent_ReturnsFalse_WhenGoVerbWithoutDirectional(string message)
    {
        var result = NavigationIntentDetector.IsNavigationIntent(message);

        Assert.That(result, Is.False, $"Expected false for: {message}");
    }

    [Test]
    public void Configure_AddsPluginNavigationPhrases_DetectedAsNavigation()
    {
        NavigationIntentDetector.Configure(
            questionLeads: [],
            navigationPhrases: ["przejdź do", "otwórz"]);

        Assert.That(NavigationIntentDetector.IsNavigationIntent("przejdź do ustawień"), Is.True);
        Assert.That(NavigationIntentDetector.IsNavigationIntent("otwórz profil"), Is.True);
    }

    [Test]
    public void Configure_AddsPluginQuestionLeads_SuppressesDetection()
    {
        NavigationIntentDetector.Configure(
            questionLeads: ["jak", "co"],
            navigationPhrases: []);

        Assert.That(NavigationIntentDetector.IsNavigationIntent("jak przejść do ustawień"), Is.False);
    }
}
