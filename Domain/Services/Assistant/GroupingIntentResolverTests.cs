// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupingIntentResolver — verifies it guarantees the real grouping skills on
/// geographic grouping / assignment requests and stays silent on read-only group questions so the
/// tool set is not needlessly widened.
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
    public void GuaranteedSkillNames_Returns_GroupingSkills_For_GroupingIntent(string message)
    {
        var result = GroupingIntentResolver.GuaranteedSkillNames(message);

        result.ShouldContain("propose_employee_grouping");
        result.ShouldContain("apply_employee_grouping");
        result.ShouldContain("add_client_to_nearest_group");
    }

    [TestCase("Welche Gruppen gibt es?")]
    [TestCase("Zeig mir die Gruppen")]
    [TestCase("Erstelle eine Gruppe Bern")]
    [TestCase("Liste alle Gruppen")]
    [TestCase("Wie viele Mitarbeiter gibt es?")]
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
}
