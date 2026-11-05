// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deterministic entry into the ERP hand-over skills: a natural question about how an ERP or a named ERP
/// product (SAP, Abacus) gets its orders into Klacks guarantees the settings read and the page explanation in the
/// toolset whatever the retrieval returned (and past the cap of the trigger keyword guarantee, where the many skills
/// that match "order" outrank a single ERP term), in all four core languages. A message without an ERP word, or with
/// the letters inside another word, guarantees none of them, and both guaranteed names are real seeded skills.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ErpHandoverKeywordRoutingTests
{
    [TestCase("Wie bringe ich Bestellungen aus unserem ERP in Klacks?")]
    [TestCase("Unser ERP soll die Bestellungen an Klacks übergeben. Welcher Weg passt bei uns?")]
    [TestCase("Wir wollen unser ERP an Klacks anbinden.")]
    [TestCase("Kann ich Bestellungen aus dem ERP importieren?")]
    [TestCase("Wie kommen Aufträge aus SAP in Klacks?")]
    [TestCase("Wie landen die Aufträge aus Abacus bei uns in Klacks?")]
    [TestCase("Gibt es eine ERP-Schnittstelle?")]
    [TestCase("How do I get orders from our ERP into Klacks?")]
    [TestCase("Our ERP should hand the orders over to Klacks. Which way fits our setup?")]
    [TestCase("We want to connect our ERP to Klacks.")]
    [TestCase("Can I import orders from the ERP?")]
    [TestCase("How do orders from SAP end up in Klacks?")]
    [TestCase("Is there an ERP interface?")]
    [TestCase("Comment importer les commandes de notre ERP dans Klacks ?")]
    [TestCase("Notre ERP doit transmettre les commandes à Klacks. Quelle voie convient chez nous ?")]
    [TestCase("Nous voulons connecter notre ERP à Klacks.")]
    [TestCase("Comment les commandes de SAP arrivent-elles dans Klacks ?")]
    [TestCase("Existe-t-il une interface ERP ?")]
    [TestCase("Come porto gli ordini dal nostro ERP in Klacks?")]
    [TestCase("Il nostro ERP deve consegnare gli ordini a Klacks. Quale via è adatta alla nostra installazione?")]
    [TestCase("Vogliamo collegare il nostro ERP a Klacks.")]
    [TestCase("Come arrivano gli ordini da SAP in Klacks?")]
    [TestCase("Esiste un'interfaccia ERP?")]
    public void ErpHandoverQuestion_GuaranteesTheSettingsReadAndThePageExplanation(string message)
    {
        var guaranteed = ConceptExplainSkillKeywords.ResolveSkillNames(message);

        guaranteed.ShouldContain(SkillNames.GetErpDropPointSettings);
        guaranteed.ShouldContain(SkillNames.ExplainPageSettingsErpDropPoints);
    }

    [TestCase("Wie importiere ich Mitarbeiter aus einer Excel-Datei?")]
    [TestCase("Das ist verpflichtend für alle, kein Sapperlot, nur ein Saphir.")]
    [TestCase("Das war eine rapide Entwicklung und eine gute Idee.")]
    public void MessageWithoutAnErpWord_GuaranteesNoHandoverSkill(string message)
    {
        var guaranteed = ConceptExplainSkillKeywords.ResolveSkillNames(message);

        guaranteed.ShouldNotContain(SkillNames.GetErpDropPointSettings);
        guaranteed.ShouldNotContain(SkillNames.ExplainPageSettingsErpDropPoints);
    }

    [Test]
    public void TheGuaranteedNames_AreSeededSkills()
    {
        var seeded = SeedSkillCatalog.Load().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        seeded.ShouldContain(SkillNames.GetErpDropPointSettings);
        seeded.ShouldContain(SkillNames.ExplainPageSettingsErpDropPoints);
    }
}
