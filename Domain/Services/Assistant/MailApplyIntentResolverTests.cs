// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// MailApplyIntentResolver: a message asks to enter what e-mails contain only when it carries an enter/apply verb, names
/// a thing to enter (a referent or availabilities, wishes, keywords) and has the e-mails as its source, through a mail
/// word in the message or a mail read by the previous tool-calling turn. Covered in de/en/fr/it; an information question
/// and a message missing any of the three signals never count.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class MailApplyIntentResolverTests
{
    private static readonly string[] AfterMailRead = ["list_emails", "read_email", "read_email"];

    [TestCase("Trag das bitte alles ein: Verfügbarkeiten zu den Verfügbarkeiten, Wünsche als Keywords in den Plan.")]
    [TestCase("Trag bitte die Verfügbarkeiten von Mia und Priya ein und die Wünsche von Jonas und Lena als Keywords in den Plan.")]
    [TestCase("Übernimm bitte alles aus diesen Mails in Klacks.")]
    [TestCase("Enter all of that, availabilities as availabilities and the wishes as keywords.")]
    [TestCase("Apply everything from the mails to Klacks.")]
    [TestCase("Saisis tout ça dans Klacks, les disponibilités et les souhaits comme mots-clés.")]
    [TestCase("Enregistre toutes les disponibilités du mail dans Klacks.")]
    [TestCase("Inserisci tutto in Klacks: disponibilità e desideri come parole chiave.")]
    [TestCase("Registra le disponibilità della posta in Klacks.")]
    public void EnterRequest_AfterTheMailsWereRead_IsMailApply(string message)
    {
        MailApplyIntentResolver.IsMailApply(message, AfterMailRead).ShouldBeTrue();
    }

    [TestCase("Lies die Mails vom Team und trag die Verfügbarkeiten ein.")]
    [TestCase("Read the team's e-mails and enter everything in Klacks.")]
    [TestCase("Lis les courriels de l'équipe et saisis tout dans Klacks.")]
    [TestCase("Leggi le email del team e inserisci tutto in Klacks.")]
    public void EnterRequest_NamingTheMails_IsMailApply_WithoutAPreviousTurn(string message)
    {
        MailApplyIntentResolver.IsMailApply(message, null).ShouldBeTrue();
    }

    [TestCase("Was steht in den neuen E-Mails vom Team?")]
    [TestCase("Wie trage ich die Verfügbarkeiten aus den Mails ein?")]
    [TestCase("What do the mails say about availability?")]
    [TestCase("Qu'est-ce que les courriels disent sur les disponibilités ?")]
    public void InformationQuestion_IsNeverMailApply(string message)
    {
        MailApplyIntentResolver.IsMailApply(message, AfterMailRead).ShouldBeFalse();
    }

    [Test]
    public void EnterRequest_WithoutAnyMailContext_IsNotMailApply()
    {
        MailApplyIntentResolver.IsMailApply("Trag das bitte alles ein: Verfügbarkeiten und Wünsche.", null).ShouldBeFalse();
        MailApplyIntentResolver.IsMailApply("Trag die Verfügbarkeit von Priya Nair ein.", ["add_client_note"]).ShouldBeFalse();
    }

    [Test]
    public void MailContextWithoutAnEnterVerb_IsNotMailApply()
    {
        MailApplyIntentResolver.IsMailApply("Fass alles aus den Mails zusammen.", AfterMailRead).ShouldBeFalse();
        MailApplyIntentResolver.IsMailApply("Zeig mir die Verfügbarkeiten.", AfterMailRead).ShouldBeFalse();
    }

    [Test]
    public void MailContextAndVerbWithoutAThingToEnter_IsNotMailApply()
    {
        MailApplyIntentResolver.IsMailApply("Trag bitte einen neuen Mitarbeiter Max Muster ein.", AfterMailRead).ShouldBeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void BlankMessage_IsNotMailApply(string? message)
    {
        MailApplyIntentResolver.IsMailApply(message, AfterMailRead).ShouldBeFalse();
    }

    [Test]
    public void TheGuaranteedSkills_AreThePersonLookupTheThreeWritesAndTheScheduleView()
    {
        MailApplyIntentResolver.GuaranteedSkillNames.ShouldBe(
        [
            "search_employees", "set_client_availability", "add_schedule_command", "add_schedule_commands_range", "open_schedule"
        ]);
    }
}
