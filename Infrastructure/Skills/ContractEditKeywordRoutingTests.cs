// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deterministic reach of update_contract for everyday edit phrasings of an existing contract
/// (working weekdays, holiday calendar), against the real seed catalog. Live 2026-10-10: "Beim Vertrag ... soll
/// ab sofort auch samstags gearbeitet werden" and "Und stell bei dem Vertrag den Feiertagskalender auf Zürich um"
/// left update_contract out of a 30-tool toolset, because every contract skill matched only the bare word
/// "vertrag" and five calendar skills matched "feiertagskalender" with more distinct terms. The guard asserts
/// that update_contract is inside the keyword-guarantee cap for those sentences and for paraphrases of them.
/// The second guard proves the contract-edit phrases do not pull update_contract into the guarantee for
/// plain questions about contracts: an Act skill must not enter the toolset out of curiosity, so no bare
/// "in the contract" locative and no bare French or Italian word for contract is a synonym of update_contract.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ContractEditKeywordRoutingTests
{
    private const string UpdateContractSkill = "update_contract";

    [TestCase("Beim Vertrag 'Vollzeit 160 BE' soll ab sofort auch samstags gearbeitet werden.")]
    [TestCase("Bei dem Vertrag Teilzeit 80 soll künftig auch sonntags gearbeitet werden.")]
    [TestCase("Der Vertrag soll künftig nur noch Montag bis Mittwoch als Arbeitstage haben.")]
    [TestCase("Ändere die Arbeitstage des Vertrags Vollzeit 160 BE auf Montag bis Freitag.")]
    [TestCase("On the contract 'Full time 160' Saturdays should be worked from now on.")]
    [TestCase("Change the working days of the contract Full time 160 to Monday through Thursday.")]
    [TestCase("Sur le contrat Temps plein 160, il faut aussi travailler le samedi.")]
    [TestCase("Sul contratto Tempo pieno 160 si deve lavorare anche il sabato.")]
    public void EditingTheWorkingWeekdaysOfAContract_GuaranteesUpdateContract(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(UpdateContractSkill);
    }

    [TestCase("Und stell bei dem Vertrag den Feiertagskalender auf Zürich um.")]
    [TestCase("Ändere den Vertrag 'Vollzeit 160 BE' bitte so, dass er den Feiertagskalender Zürich verwendet.")]
    [TestCase("Der Vertrag soll künftig den Feiertagskalender Zürich verwenden.")]
    [TestCase("Stell den Feiertagskalender des Vertrags Teilzeit 80 auf Bern um.")]
    [TestCase("Beim Vertrag Vollzeit 160 BE soll der Feiertagskalender auf Zürich gesetzt werden.")]
    [TestCase("Set the holiday calendar of the contract Full time 160 to Zurich.")]
    [TestCase("Change the contract so that it uses the Zurich holiday calendar.")]
    [TestCase("Modifier le contrat Temps plein 160 pour utiliser le calendrier des jours fériés de Zurich.")]
    [TestCase("Modificare il contratto Tempo pieno 160: il calendario festivo del contratto deve essere Zurigo.")]
    public void SwitchingTheHolidayCalendarOfAContract_GuaranteesUpdateContract(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(UpdateContractSkill);
    }

    [TestCase("Welche Verträge gibt es?")]
    [TestCase("Was steht im Vertrag Vollzeit 160 BE?")]
    [TestCase("What is in the contract?")]
    [TestCase("Qu'est-ce qu'un contrat dans Klacks ?")]
    [TestCase("Cos'è un contratto in Klacks?")]
    [TestCase("Zeig mir die Details des Vertrags Vollzeit 160 BE.")]
    [TestCase("Which contracts do we have?")]
    [TestCase("Show me the contract details of Full time 160.")]
    public void PlainReadQuestionAboutContracts_DoesNotGuaranteeUpdateContract(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldNotContain(UpdateContractSkill);
    }
}
