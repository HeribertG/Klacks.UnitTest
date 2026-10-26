// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
    // W4: German imperative forms of mutation verbs.
    [TestCase("Leg die Spamregel an")]
    [TestCase("Mach eine neue Spamregel")]
    [TestCase("Setz den Haken bei Überstunden")]
    [TestCase("Füg den Kunden der Gruppe hinzu")]
    [TestCase("Stell die Standardsprache ein")]
    [TestCase("Trag den Kunden ein")]
    [TestCase("create a customer named Acme Ltd")]
    [TestCase("delete client 5")]
    [TestCase("add the employee to the Bern group")]
    [TestCase("cut the 24h order into three parts")]
    [TestCase("assign the contract to the customer")]
    [TestCase("créer un nouveau client")]
    [TestCase("crea un nuovo cliente")]
    [TestCase("Korrigier den Text der Plannotiz bei Herrn Rey vom Mittwoch.")]
    [TestCase("Die Postleitzahl in der Adresse von Frau Roth ist falsch, bitte berichtigen.")]
    [TestCase("Sperr alle Mails von der Domain werbung-xyz.com als Spam.")]
    [TestCase("Der Planvorschlag taugt nichts, verwirf ihn.")]
    [TestCase("Wir brauchen das italienische Sprachpaket nicht mehr, bitte deaktivieren.")]
    [TestCase("Streich den Einsatz von Herrn Lang am Donnerstag wieder aus dem Plan.")]
    [TestCase("Stopp den laufenden Planungslauf, das dauert mir zu lange.")]
    [TestCase("Fixier den Dienst von Herrn Meier am Freitag.")]
    [TestCase("Schalt das installierte SMS-Modul jetzt scharf.")]
    [TestCase("Häng den Frühdienst vom Montag an die Gruppe Pflege Ost an.")]
    [TestCase("Stell meine Anzeigesprache bitte auf Französisch um.")]
    [TestCase("Benenn die Gruppe 'Team A' in 'Frühschicht Zentrum' um.")]
    [TestCase("Richte eine Regel ein, die höchstens vier Nachtdienste erlaubt.")]
    [TestCase("Hol den gelöschten Dienst von Müller wieder zurück")]
    [TestCase("Merk dir bitte, dass unser Geschäftsjahr am 1. April beginnt.")]
    [TestCase("Bündel alle mobilen Dienste rund um Winterthur in einen Container.")]
    [TestCase("Übernimm die vorgeschlagene Standortzuordnung jetzt fest.")]
    [TestCase("Teil den 24-Stunden-Auftrag von Frau Marti in zwei Teile auf.")]
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
    [TestCase("Erklär mir die Schaltfläche im Dienstformular")]
    [TestCase("Zeig mir die Merkmale der Gruppe Nord")]
    [TestCase("Zeig mir die Sperrzeiten der Gruppe Nord")]
    [TestCase("Zeig mir die Einrichtung der Gruppe Nord")]
    [TestCase("Liste die installierten Plugins")]
    [TestCase("Hol mir die Adressen aller Mitarbeiter")]
    [TestCase("Teil mir mit, wer morgen frei hat")]
    [TestCase("Benenne die Mitarbeiter, die in Bern wohnen")]
    [TestCase("Ist bei uns ein DeepL-Schlüssel hinterlegt?")]
    [TestCase("Gib mir die Liste der Mitarbeiter in Bern")]
    [TestCase("Geh die offenen Bestellungen mit mir durch, bevor etwas versiegelt wird.")]
    [TestCase("Wann startet der nächste ERP-Import?")]
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
    [TestCase("cosa significa sigillare un ordine?")]
    [TestCase("cos'è un contratto?")]
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

    [Test]
    public void PluginQuestionLead_MatchesOnlyAWholeLeadingWord()
    {
        MutationIntentDetector.Configure(questionLeads: ["co"], mutationPhrases: []);

        MutationIntentDetector.IsInformationQuestion("co to jest kontrakt?").ShouldBeTrue();
        MutationIntentDetector.IsInformationQuestion("Could you create a customer named Acme Ltd").ShouldBeFalse();
        MutationIntentDetector.IsMutationIntent("Could you create a customer named Acme Ltd").ShouldBeTrue();
    }

    [Test]
    public void PluginQuestionLead_InLatinScript_NeedsAWordBoundary()
    {
        MutationIntentDetector.Configure(questionLeads: ["ce", "ai"], mutationPhrases: []);

        MutationIntentDetector.IsInformationQuestion("ce este un contract?").ShouldBeTrue();
        MutationIntentDetector.IsInformationQuestion("Cette semaine, crée un client").ShouldBeFalse();
        MutationIntentDetector.IsInformationQuestion("aide-moi à créer un client").ShouldBeFalse();
    }

    [Test]
    public void PluginQuestionLead_InOtherScripts_StillMatchesAsPrefix()
    {
        MutationIntentDetector.Configure(questionLeads: ["どうやって", "อะไร", "מה"], mutationPhrases: []);

        MutationIntentDetector.IsInformationQuestion("どうやって契約を作成しますか？").ShouldBeTrue();
        MutationIntentDetector.IsInformationQuestion("อะไรคือสัญญา").ShouldBeTrue();
        MutationIntentDetector.IsInformationQuestion("מהו חוזה?").ShouldBeTrue();
    }

    [Test]
    public void PluginMutationPhrase_StillMatchesInsideAffixedWords()
    {
        MutationIntentDetector.Configure(questionLeads: [], mutationPhrases: ["buat", "作成"]);

        MutationIntentDetector.IsMutationIntent("tolong membuat kontrak untuk Budi").ShouldBeTrue();
        MutationIntentDetector.IsMutationIntent("契約を作成して").ShouldBeTrue();
    }
}
