// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CompletionClaimDetector — verifies it fires on assistant answers that claim a state
/// change already happened (de/en/fr/it) and, crucially (precision bias), stays silent on explanations,
/// offers, future tense and questions so the no-action notice is not appended to honest replies.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class CompletionClaimDetectorTests
{
    [TestCase("Ich habe den Kunden Müller AG angelegt.")]
    [TestCase("Der Dienst wurde erstellt.")]
    [TestCase("Ich habe die Adresse aktualisiert.")]
    [TestCase("Der Mitarbeiter wurde der Gruppe hinzugefügt.")]
    [TestCase("Die Bestellung ist erledigt.")]
    [TestCase("Erledigt!")]
    [TestCase("I have created the client.")]
    [TestCase("The client has been created.")]
    [TestCase("I've added the phone number.")]
    [TestCase("The client — it's been created.")]
    [TestCase("The employee has been removed.")]
    [TestCase("Done!")]
    [TestCase("J'ai créé le client.")]
    [TestCase("Le client a été supprimé.")]
    [TestCase("C'est fait.")]
    [TestCase("Ho creato il cliente.")]
    [TestCase("Il cliente è stato eliminato.")]
    [TestCase("Completato.")]
    public void ClaimsCompletion_True_For_PastCompletionClaims(string response)
    {
        CompletionClaimDetector.ClaimsCompletion(response).ShouldBeTrue(response);
    }

    [TestCase("So wird ein Kunde angelegt: Klicken Sie auf Neu.")]
    [TestCase("Ein Kunde ist mit wenigen Klicks angelegt.")]
    [TestCase("Die Nummer ist im Profil gespeichert, wenn du das Formular ausfüllst.")]
    [TestCase("A client is created by clicking the New button.")]
    [TestCase("To store a phone, the number is saved automatically.")]
    [TestCase("Le client est créé en un seul clic.")]
    [TestCase("A new client — it's created by clicking the New button.")]
    [TestCase("That's changed in the settings dialog.")]
    [TestCase("Um einen Kunden anzulegen, brauche ich noch den Namen.")]
    [TestCase("Ich kann den Kunden anlegen, wenn du möchtest.")]
    [TestCase("Ich werde den Kunden gleich anlegen.")]
    [TestCase("Soll ich den Kunden anlegen?")]
    [TestCase("I can create the client for you.")]
    [TestCase("I will add the phone number next.")]
    [TestCase("Which group should the employee join?")]
    [TestCase("Guten Morgen! Wie kann ich helfen?")]
    [TestCase("Hier sind die vorhandenen Gruppen.")]
    [TestCase("")]
    [TestCase("   ")]
    public void ClaimsCompletion_False_For_Explanations_Offers_Futures_And_Questions(string response)
    {
        CompletionClaimDetector.ClaimsCompletion(response).ShouldBeFalse(response);
    }

    [Test]
    public void ClaimsCompletion_False_For_Null()
    {
        CompletionClaimDetector.ClaimsCompletion(null).ShouldBeFalse();
    }
}
