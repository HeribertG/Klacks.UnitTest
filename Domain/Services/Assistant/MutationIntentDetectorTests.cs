// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MutationIntentDetector — verifies it fires on real state-changing requests
/// across languages and, crucially (precision bias), stays silent on information questions and
/// read-only requests so the well-behaved default path is never forced into a spurious tool call.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class MutationIntentDetectorTests
{
    [TestCase("Erfasse einen 24h-Dienst geteilt in 3")]
    [TestCase("Erstelle einen neuen Kunden Müller AG")]
    [TestCase("Lege einen Kunden namens Acme AG an")]
    [TestCase("Kannst du mir einen Kunden anlegen?")]
    [TestCase("Füge den Mitarbeiter zur Gruppe Bern hinzu")]
    [TestCase("Lösche die Bestellung 42")]
    [TestCase("Ändere die Startzeit auf 08:00")]
    [TestCase("Schneide den Dienst in drei Teile")]
    [TestCase("Weise den Vertrag dem Kunden zu")]
    [TestCase("Plane den Frühdienst für Montag ein")]
    [TestCase("Ordne die Mitarbeiter den Gruppen zu")]
    [TestCase("Ordne die Mitarbeiter nach ihrer Adresse den passenden Gruppen zu")]
    [TestCase("Gruppiere die Mitarbeiter nach Adresse")]
    [TestCase("create a customer named Acme Ltd")]
    [TestCase("delete client 5")]
    [TestCase("add the employee to the Bern group")]
    [TestCase("cut the 24h order into three parts")]
    [TestCase("assign the contract to the customer")]
    [TestCase("créer un nouveau client")]
    [TestCase("crea un nuovo cliente")]
    public void IsMutationIntent_True_For_StateChanging_Requests(string message)
    {
        MutationIntentDetector.IsMutationIntent(message).ShouldBeTrue(message);
    }

    [TestCase("Wie erstelle ich einen Kunden?")]
    [TestCase("Was ist eine Bestellung?")]
    [TestCase("Warum wurde der Dienst geteilt?")]
    [TestCase("Wie viele Kunden gibt es?")]
    [TestCase("Zeig mir die Adressen")]
    [TestCase("Zeige die offenen Bestellungen")]
    [TestCase("show me the orders")]
    [TestCase("how do I create a customer?")]
    [TestCase("what does cut_shift do?")]
    [TestCase("Hallo, wie geht es dir?")]
    [TestCase("Welche Kunden sind in Bern?")]
    [TestCase("Zeig mir die Gruppen")]
    [TestCase("Welche Gruppen gibt es?")]
    [TestCase("")]
    [TestCase("   ")]
    public void IsMutationIntent_False_For_Questions_And_Reads(string message)
    {
        MutationIntentDetector.IsMutationIntent(message).ShouldBeFalse(message);
    }

    [Test]
    public void IsMutationIntent_False_For_Null()
    {
        MutationIntentDetector.IsMutationIntent(null).ShouldBeFalse();
    }

    [TestCase("Welche Gruppen gibt es?")]
    [TestCase("Wie erstelle ich einen Kunden?")]
    [TestCase("Was ist eine Bestellung?")]
    [TestCase("Wie viele Kunden gibt es?")]
    [TestCase("which groups exist?")]
    [TestCase("how do I create a customer?")]
    [TestCase("quelle est l'adresse?")]
    [TestCase("quali gruppi ci sono?")]
    public void IsInformationQuestion_True_For_Questions(string message)
    {
        MutationIntentDetector.IsInformationQuestion(message).ShouldBeTrue(message);
    }

    [TestCase("Zeig mir die Adressen")]
    [TestCase("Erstelle einen neuen Kunden Müller AG")]
    [TestCase("Neuen Mitarbeiter, bitte.")]
    [TestCase("Füge den Mitarbeiter zur Gruppe hinzu")]
    [TestCase("")]
    [TestCase("   ")]
    public void IsInformationQuestion_False_For_NonQuestions(string message)
    {
        // Crucial boundary: a display imperative ("Zeig mir …") and a verbless action request
        // ("Neuen Mitarbeiter, bitte") are NOT information questions — the recipe gate must let them
        // through so genuine (terse) actions still reach the guided flow.
        MutationIntentDetector.IsInformationQuestion(message).ShouldBeFalse(message);
    }

    [Test]
    public void IsInformationQuestion_False_For_Null()
    {
        MutationIntentDetector.IsInformationQuestion(null).ShouldBeFalse();
    }

    [Test]
    public void IsMutationIntent_Does_Not_Misfire_On_Word_Address()
    {
        // "add" is matched as an exact token only — it must not trip on the substring inside "address".
        MutationIntentDetector.IsMutationIntent("Bitte die Adresse aktualisieren").ShouldBeTrue();
        MutationIntentDetector.IsMutationIntent("Zeig mir die address liste").ShouldBeFalse();
    }

    [Test]
    public void Configure_AddsPluginMutationPhrases_DetectedAsMutation()
    {
        MutationIntentDetector.Configure(
            questionLeads: [],
            mutationPhrases: ["usuń klienta", "utwórz pracownika"]);

        MutationIntentDetector.IsMutationIntent("usuń klienta Müller AG").ShouldBeTrue();
        MutationIntentDetector.IsMutationIntent("proszę utwórz pracownika Jan Kowalski").ShouldBeTrue();
    }

    [Test]
    public void Configure_AddsPluginQuestionLeads_SuppressesDetection()
    {
        MutationIntentDetector.Configure(
            questionLeads: ["jak", "dlaczego"],
            mutationPhrases: []);

        MutationIntentDetector.IsMutationIntent("jak usunąć klienta?").ShouldBeFalse();
    }
}
