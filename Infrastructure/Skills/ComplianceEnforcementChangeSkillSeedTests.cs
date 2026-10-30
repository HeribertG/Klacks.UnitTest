// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights and shape of evaluate_compliance_enforcement_change: it is gated on the Admin role like
/// list_period_issues, whose findings it counts, and no role below Admin holds the settings right it would otherwise need.
/// It is a read-only Query with the Advise effect in the
/// settings area, offers exactly the rules of the catalog, has the AdvisesFor edge to the settings update (which the
/// risk classifier keeps Sensitive, so the change is confirmed on its own) and a description that names no neighbour.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.ComplianceEnforcement;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ComplianceEnforcementChangeSkillSeedTests
{
    private const string AdviceSkill = "evaluate_compliance_enforcement_change";
    private const string ActSkill = "update_compliance_enforcement_settings";
    private const string IssuesSkill = "list_period_issues";
    private const int AdvisesForType = 3;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] Neighbours =
    [
        ActSkill, IssuesSkill, "detect_conflicts", "preview_company_rule", "evaluate_period_close_readiness", "close_period"
    ];

    [Test]
    public void AdviceSkill_IsGatedOnTheAdminRole()
    {
        Rights(AdviceSkill).ShouldBe([Roles.Admin]);
    }

    [Test]
    public void SettingsViewRight_IsHeldByTheAdminRoleOnly_SoTheReachEqualsTheFindingList()
    {
        Rights(IssuesSkill).ShouldBe([Roles.Admin]);
        Permissions.GetPermissionsForRole(Roles.Admin).ShouldContain(Permissions.CanViewSettings);
        Permissions.GetPermissionsForRole(Roles.Admin).ShouldContain(Permissions.CanViewShifts);
        Permissions.GetPermissionsForRole(Roles.Authorised).ShouldNotContain(Permissions.CanViewSettings);
        Permissions.PlannerFloor.ShouldNotContain(Permissions.CanViewSettings);
        Permissions.ExpandRoles([]).ShouldNotContain(Permissions.CanViewSettings);
    }

    [Test]
    public void AdviceSkill_IsAReadOnlyQueryWithTheAdviseEffectInTheSettingsArea()
    {
        var skill = Seeded(AdviceSkill);

        skill.GetProperty("category").GetString().ShouldBe("Query");
        skill.GetProperty("effect").GetString().ShouldBe("Advise");
        skill.GetProperty("area").GetString().ShouldBe("settings");
        skill.GetProperty("alwaysOn").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public void RuleParameter_IsOptional_AndOffersExactlyTheRulesOfTheCatalog()
    {
        var parameter = Seeded(AdviceSkill).GetProperty("parameters").EnumerateArray().Single();

        parameter.GetProperty("name").GetString().ShouldBe("rule");
        parameter.GetProperty("required").GetBoolean().ShouldBeFalse();
        parameter.GetProperty("enumValues").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(ComplianceRuleCatalog.Rules);
    }

    [Test]
    public void AdviceSkill_HasAnAdvisesForEdgeToTheSettingsUpdate()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-relation-seeds.json")));

        document.RootElement.GetProperty("relations").EnumerateArray().ShouldContain(relation =>
            relation.GetProperty("skillAName").GetString() == AdviceSkill &&
            relation.GetProperty("skillBName").GetString() == ActSkill &&
            relation.GetProperty("type").GetInt32() == AdvisesForType);
    }

    [Test]
    public void TheAdvisedChange_StaysSensitive_SoTheDescriptionMayPromiseASeparateConfirmedStep()
    {
        SkillRiskClassifier.SensitiveSkills.ShouldContain(ActSkill);
        Seeded(AdviceSkill).GetProperty("description").GetString()!.ShouldContain("separate step the system confirms once");
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
    public void AdviceSkill_HasLabelsSynonymsAndTriggerKeywordsInTheFourCoreLanguages()
    {
        var skill = Seeded(AdviceSkill);

        foreach (var language in new[] { "de", "en", "fr", "it" })
        {
            skill.GetProperty("labels").GetProperty(language).GetString().ShouldNotBeNullOrWhiteSpace();
            skill.GetProperty("synonyms").GetProperty(language).GetArrayLength().ShouldBeGreaterThan(0);
            skill.GetProperty("triggerKeywords").GetProperty(language).GetArrayLength().ShouldBeGreaterThan(0);
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
