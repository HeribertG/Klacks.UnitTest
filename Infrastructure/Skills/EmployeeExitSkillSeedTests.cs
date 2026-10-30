// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights and shape of evaluate_employee_exit: it asks for the rights to see the person and the plan
/// (not the right to edit clients that recording the exit needs), is a read-only Query with the Advise effect in the
/// client area, has an AdvisesFor edge to the recording skill, and its description never names its neighbours (see
/// skill-discoverability.md section 4).
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class EmployeeExitSkillSeedTests
{
    private const string AdviceSkill = "evaluate_employee_exit";
    private const string RecordSkill = "end_client_membership";
    private const int AdvisesForType = 3;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] Neighbours =
    [
        RecordSkill, "list_client_memberships", "update_membership", "delete_membership", "search_employees", "delete_client"
    ];

    [Test]
    public void AdviceSkill_NeedsToSeeTheClientsAndTheShifts_AndTheRecordingNeedsToEditClients()
    {
        Rights(AdviceSkill).ShouldBe(["CanViewClients", "CanViewShifts"]);
        Rights(RecordSkill).ShouldContain("CanEditClients");
    }

    [Test]
    public void AdviceSkill_IsAReadOnlyQueryWithTheAdviseEffectInTheClientArea()
    {
        var skill = Seeded(AdviceSkill);

        skill.GetProperty("category").GetString().ShouldBe("Query");
        skill.GetProperty("effect").GetString().ShouldBe("Advise");
        skill.GetProperty("area").GetString().ShouldBe("client");
        skill.GetProperty("alwaysOn").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public void AdviceSkill_RequiresTheExitDateAndNothingElse()
    {
        var required = Seeded(AdviceSkill).GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("required").GetBoolean())
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToList();

        required.ShouldBe(["exitDate"]);
    }

    [Test]
    public void AdviceSkill_HasAnAdvisesForEdgeToTheRecordingSkill()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-relation-seeds.json")));

        document.RootElement.GetProperty("relations").EnumerateArray().ShouldContain(relation =>
            relation.GetProperty("skillAName").GetString() == AdviceSkill &&
            relation.GetProperty("skillBName").GetString() == RecordSkill &&
            relation.GetProperty("type").GetInt32() == AdvisesForType);
    }

    [Test]
    public void AdviceDescription_NeverNamesItsNeighbours()
    {
        var description = Seeded(AdviceSkill).GetProperty("description").GetString()!;

        foreach (var neighbour in Neighbours)
        {
            description.ShouldNotContain(neighbour);
        }
    }

    [Test]
    public void AdviceSkill_CarriesLabelsAndSynonymsInAllFourCoreLanguages()
    {
        var skill = Seeded(AdviceSkill);

        foreach (var language in new[] { "de", "en", "fr", "it" })
        {
            skill.GetProperty("labels").GetProperty(language).GetString().ShouldNotBeNullOrWhiteSpace();
            skill.GetProperty("synonyms").GetProperty(language).GetArrayLength().ShouldBeGreaterThan(0);
        }
    }

    private static List<string?> Rights(string skillName) =>
        Seeded(skillName).GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToList();

    private static JsonElement Seeded(string skillName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-seeds.json")));

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");
        return skill.Clone();
    }

    private static string Locate(string fileName) =>
        RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, fileName]);
}
