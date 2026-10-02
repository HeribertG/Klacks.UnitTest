// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GroupingIntentResolver — verifies it guarantees the real grouping skills, including
/// the two that give a group coordinates, on geographic grouping / assignment requests, and stays
/// silent on read-only group questions so the tool set is not needlessly widened.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class GroupingIntentResolverTests
{
    [TestCase("Gruppiere die Mitarbeiter nach Adresse")]
    [TestCase("Ordne die Mitarbeiter nach ihrer Adresse den passenden Gruppen zu")]
    [TestCase("Kunden nach Region gruppieren")]
    [TestCase("assign the employees to the nearest group")]
    [TestCase("group the customers by location")]
    [TestCase("Kannst du Mitarbeiter die noch zu keiner Gruppe gehören, gemäss ihrer Adresse zu Gruppen zuordnen")]
    public void GuaranteedSkillNames_Returns_GroupingSkills_For_GroupingIntent(string message)
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames(message);

        result.ShouldContain("propose_grouping");
        result.ShouldContain("apply_grouping");
        result.ShouldContain("add_client_to_nearest_group");
        result.ShouldContain("group_ungrouped_by_city_name");
        result.ShouldContain("assign_shifts_to_city_groups");
    }

    [TestCase("Gruppiere die Mitarbeiter nach Adresse")]
    [TestCase("Kunden nach Region gruppieren")]
    [TestCase("Bestimme die Standorte der Gruppen automatisch")]
    [TestCase("determine the location of the groups automatically")]
    [TestCase("Setze den Standort der Gruppe Bern")]
    public void GuaranteedSkillNames_Returns_GeocodingSkills_For_GroupingIntent(string message)
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames(message);

        result.ShouldContain("geocode_location_groups");
        result.ShouldContain("set_group_location");
        result.ShouldContain("check_group_geocoding_status");
    }

    [TestCase("Erstelle Standortgruppen aus allen Adressen von Mitarbeitern, Kunden und Externen")]
    [TestCase("create location groups from all customer and employee addresses")]
    public void GuaranteedSkillNames_GroupingIntent_IncludesPartitionClientsByAddress(string message)
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames(message);

        result.ShouldContain("partition_clients_by_address");
    }

    [TestCase("Gruppiere die Mitarbeiter nach Qualifikation")]
    [TestCase("Erstelle Gruppen nach Qualifikationen")]
    [TestCase("group the employees by qualification")]
    [TestCase("create one group per qualification")]
    [TestCase("Regroupe les employés par qualification")]
    [TestCase("Raggruppa i dipendenti per qualifica")]
    [TestCase("Crea un gruppo per ogni qualifica")]
    public void GuaranteedSkillNames_QualificationGroupingIntent_IncludesPartitionClientsByQualification(string message)
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames(message);

        result.ShouldContain("partition_clients_by_qualification");
        result.ShouldContain("list_groups");
    }

    [TestCase("Welche Qualifikationen hat die Gruppe Bern?")]
    [TestCase("which qualifications does the group Bern have?")]
    [TestCase("Bern 组的员工有哪些资格？")]
    [TestCase("ما هي مؤهلات مجموعة برن؟")]
    [TestCase("Ποια προσόντα έχει η ομάδα Bern;")]
    public void GuaranteedSkillNames_Empty_For_QualificationQuestionsAboutAGroup(string message)
    {
        GroupingIntentResolver.GuaranteedSkillNames(message).ShouldBeEmpty();
    }

    [Test]
    public void GuaranteedSkillNames_GuaranteedSet_StaysSmallEnoughForTheToolCeiling()
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames("Gruppiere die Mitarbeiter nach Adresse");

        result.Count.ShouldBe(11);
        result.Distinct().Count().ShouldBe(result.Count);
    }

    [TestCase("Kannst du Dienste gemäss ihrer Kundenadresse auf die Gruppen aufteilen?")]
    [TestCase("Dienste nach Kundenadresse auf die Gruppen aufteilen")]
    [TestCase("Alle Dienste zu allen Gruppen/Städten")]
    [TestCase("Alle Dienste zu allen Städten")]
    [TestCase("shifts die nicht an einer Stadt oder Gemeinde angehängt sind zu einer solchen Stadt hinzufügen, wenn kein direkter Treffer die nächstgelegene")]
    [TestCase("assign all shifts to the town groups by customer address")]
    [TestCase("Répartir les services dans les communes")]
    [TestCase("Assegna i turni ai comuni più vicini")]
    public void GuaranteedSkillNames_Returns_ShiftToCityGroupsSkill_For_ShiftPlacementIntent(string message)
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames(message);

        result.ShouldContain("assign_shifts_to_city_groups");
        result.ShouldContain("list_groups");
    }

    [TestCase("Welche Dienste gibt es in der Stadt Bern?")]
    [TestCase("Zeig mir die Dienste in Zürich")]
    [TestCase("Dienste in der Stadt Bern anzeigen")]
    [TestCase("Wie viele Shifts hat die Gemeinde Bern?")]
    public void GuaranteedSkillNames_Empty_For_ShiftQuestionsWithoutPlacementIntent(string message)
    {
        GroupingIntentResolver.GuaranteedSkillNames(message).ShouldBeEmpty();
    }

    [TestCase("Ja, wende die Gruppierung an")]
    [TestCase("Ja, gruppiere sie")]
    [TestCase("yes, apply the grouping")]
    public void GuaranteedSkillNames_Returns_GroupingSkills_For_ConfirmationOnlyMessage(string message)
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames(message);

        result.ShouldContain("propose_grouping");
        result.ShouldContain("apply_grouping");
    }

    [TestCase("Welche Gruppen gibt es?")]
    [TestCase("Zeig mir die Gruppen")]
    [TestCase("Erstelle eine Gruppe Bern")]
    [TestCase("Liste alle Gruppen")]
    [TestCase("Wie viele Mitarbeiter gibt es?")]
    [TestCase("Nein, nicht gruppieren")]
    [TestCase("")]
    [TestCase("   ")]
    public void GuaranteedSkillNames_Empty_For_ReadOnly_Or_Unrelated(string message)
    {
        GroupingIntentResolver.GuaranteedSkillNames(message).ShouldBeEmpty();
    }

    [Test]
    public void GuaranteedSkillNames_Empty_For_Null()
    {
        GroupingIntentResolver.GuaranteedSkillNames(null).ShouldBeEmpty();
    }

    [Test]
    public void Configure_AddsPluginGroupingAndLocationTokens_DetectedAsGroupingIntent()
    {
        GroupingIntentResolver.Configure(
            groupingTokens: ["grupuj", "grupę"],
            locationOrAssignmentTokens: ["adres", "najbliż"]);

        var result = GroupingIntentResolver.GuaranteedSkillNames("Proszę pogrupuj klientów według adresu");

        result.ShouldContain("propose_grouping");
        result.ShouldContain("apply_grouping");
    }

    [Test]
    public void Configure_PluginLocationToken_CombinesWithCoreGroupingToken()
    {
        GroupingIntentResolver.Configure(
            groupingTokens: [],
            locationOrAssignmentTokens: ["おうち近く"]);

        var result = GroupingIntentResolver.GuaranteedSkillNames("group them by おうち近く");

        result.ShouldContain("propose_grouping");
    }

    [Test]
    public void Configure_PluginGroupingToken_CombinesWithCoreLocationToken()
    {
        GroupingIntentResolver.Configure(
            groupingTokens: ["ryhmit"],
            locationOrAssignmentTokens: []);

        var result = GroupingIntentResolver.GuaranteedSkillNames("ryhmitä asiakkaat their address");

        result.ShouldContain("propose_grouping");
    }
}
