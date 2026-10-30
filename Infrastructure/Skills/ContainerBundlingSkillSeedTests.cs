// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seed of evaluate_container_bundling: a read-only Advise query in the shift area with the permission of the
/// shift read skills, no write gate of the act skill, an AdvisesFor edge to the act skill, bound to the group visibility of
/// a real caller and no neighbour named in its description.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ContainerBundlingSkillSeedTests
{
    private const string ActSkill = "bundle_nearby_timerange_shifts_into_container";
    private const string AdviceSkill = EvaluateContainerBundlingSkill.SkillName;
    private const string ShiftReadSkill = "list_container_template";
    private const int AdvisesForType = 3;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    [Test]
    public void AdviceSkill_IsAReadOnlyQueryWithTheAdviseEffectInTheShiftArea()
    {
        var skill = Seeded(AdviceSkill);

        skill.GetProperty("category").GetString().ShouldBe("Query");
        skill.GetProperty("effect").GetString().ShouldBe("Advise");
        skill.GetProperty("area").GetString().ShouldBe("shift");
        new SkillRiskClassifier()
            .Classify(new SkillDescriptor(AdviceSkill, "guard", SkillCategory.Query, [], [], [], null))
            .ShouldBe(SkillRiskClass.ReadOnly);
    }

    [Test]
    public void AdviceSkill_NeedsThePermissionOfTheShiftReadSkills()
    {
        Permissions(AdviceSkill).ShouldBe(Permissions(ShiftReadSkill));
    }

    [Test]
    public void AdviceSkill_HasAnAdvisesForEdgeToTheActSkill()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-relation-seeds.json")));

        document.RootElement.GetProperty("relations").EnumerateArray().ShouldContain(relation =>
            relation.GetProperty("skillAName").GetString() == AdviceSkill &&
            relation.GetProperty("skillBName").GetString() == ActSkill &&
            relation.GetProperty("type").GetInt32() == AdvisesForType);
    }

    [Test]
    public void AdviceSkill_IsBoundToTheGroupVisibilityOfARealCaller()
    {
        GroupVisibilityBoundSkills.Contains(AdviceSkill).ShouldBeTrue();
    }

    [Test]
    public void Description_NamesNoOtherSkill()
    {
        var description = Seeded(AdviceSkill).GetProperty("description").GetString()!;

        description.ShouldNotContain(ActSkill);
        description.ShouldNotContain("optimize_container_route");
        description.ShouldNotContain("add_container_template_break");
    }

    private static List<string?> Permissions(string skillName) =>
        Seeded(skillName).GetProperty("requiredPermissions").EnumerateArray()
            .Select(permission => permission.GetString())
            .ToList();

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
