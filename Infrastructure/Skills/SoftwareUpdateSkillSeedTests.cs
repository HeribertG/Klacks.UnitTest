// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeds and gates of start_software_update and evaluate_software_update: both are Admin only like the
/// UpdateController, the act skill takes no parameter that could name a version or an address, is Sensitive with a
/// confirmation preview and therefore hidden from MCP, refused unattended and blocked on the Playground like the Trigger
/// endpoint; the advice is a read-only Query with the Advise effect and the AdvisesFor edge. The assistant hub tracker
/// must answer the Application port as the very same singleton.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.Mcp;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SoftwareUpdateSkillSeedTests
{
    private const string ActSkill = StartSoftwareUpdateSkill.SkillName;
    private const string AdviceSkill = EvaluateSoftwareUpdateSkill.SkillName;
    private const int AdvisesForType = 3;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    [TestCase(ActSkill)]
    [TestCase(AdviceSkill)]
    public void Skill_IsAdminOnlyLikeTheController(string skillName)
    {
        Seeded(skillName).GetProperty("requiredPermissions").EnumerateArray()
            .Select(permission => permission.GetString())
            .ShouldBe([Roles.Admin]);
    }

    [TestCase(ActSkill)]
    [TestCase(AdviceSkill)]
    public void Skill_TakesNoParameterAtAll(string skillName)
    {
        Seeded(skillName).GetProperty("parameters").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public void ActSkill_IsAMutatingSystemSkillClassifiedSensitive()
    {
        var skill = Seeded(ActSkill);

        skill.GetProperty("effect").GetString().ShouldBe("Mutate");
        skill.GetProperty("area").GetString().ShouldBe("system");
        SkillRiskClassifier.SensitiveSkills.ShouldContain(ActSkill);
        new SkillRiskClassifier().Classify(Descriptor(ActSkill, SkillCategory.Crud)).ShouldBe(SkillRiskClass.Sensitive);
    }

    [Test]
    public void ActSkill_IsHiddenFromExternalAgents()
    {
        new McpSkillExposurePolicy(new SkillRiskClassifier())
            .IsExposed(Descriptor(ActSkill, SkillCategory.Crud))
            .ShouldBeFalse();
    }

    [Test]
    public void ActSkill_IsBlockedOnThePlaygroundLikeTheTriggerEndpoint()
    {
        PlaygroundBlockedSkills.IsBlocked(ActSkill).ShouldBeTrue();
    }

    [Test]
    public void AdviceSkill_IsAReadOnlyQueryWithTheAdviseEffect()
    {
        var skill = Seeded(AdviceSkill);

        skill.GetProperty("category").GetString().ShouldBe("Query");
        skill.GetProperty("effect").GetString().ShouldBe("Advise");
        skill.GetProperty("area").GetString().ShouldBe("system");
        new SkillRiskClassifier().Classify(Descriptor(AdviceSkill, SkillCategory.Query)).ShouldBe(SkillRiskClass.ReadOnly);
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

    [TestCase(ActSkill)]
    [TestCase(AdviceSkill)]
    public void Description_NamesNoOtherSkill(string skillName)
    {
        var description = Seeded(skillName).GetProperty("description").GetString()!;

        description.ShouldNotContain(ActSkill);
        description.ShouldNotContain(AdviceSkill);
        description.ShouldNotContain("explain_software_updates");
    }

    [Test]
    public void AssistantConnectionTracker_AnswersTheApplicationPort()
    {
        typeof(IConnectedUserIdReader).IsAssignableFrom(typeof(IAssistantConnectionTracker)).ShouldBeTrue();
    }

    [Test]
    public void CompositionRoot_AliasesThePortToTheTrackerSingleton()
    {
        var program = File.ReadAllText(RepositoryRootLocator.RequireFile(["Klacks.Api", "Program.cs"]));

        program.ShouldContain("IConnectedUserIdReader>(");
        program.ShouldContain("sp => sp.GetRequiredService<IAssistantConnectionTracker>()");
    }

    private static SkillDescriptor Descriptor(string name, SkillCategory category) =>
        new(name, "guard", category, [], [], [], null);

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
