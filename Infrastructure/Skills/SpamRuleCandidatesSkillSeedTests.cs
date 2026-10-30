// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights and shape of evaluate_spam_rule_candidates: it is gated on the Admin role, which holds every right the
/// rule creation it advises on asks for and every right the mail list asks for (it reads sender domains of mail), it is a read-only Query
/// with the Advise effect in the inbox area, the AdvisesFor edge to the rule creation exists, that creation is Sensitive
/// (so "the system confirms once" is true), the skill is listed as untrusted output because sender domains come from the
/// senders, and its description names no other skill.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SpamRuleCandidatesSkillSeedTests
{
    private const string AdviceSkill = "evaluate_spam_rule_candidates";
    private const string CreateSkill = "create_spam_rule";
    private const string MailListSkill = "list_emails";
    private const string SensitiveClass = "Sensitive";
    private const int AdvisesForType = 3;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    [Test]
    public void AdviceSkill_ReachesNobodyWhoLacksARightOfTheRuleCreationOrOfTheMailList()
    {
        var advice = RightsOf(AdviceSkill);
        var create = RightsOf(CreateSkill);
        var mailList = RightsOf(MailListSkill);

        advice.ShouldBe([Roles.Admin]);
        var adminRights = Permissions.GetPermissionsForRole(Roles.Admin);
        create.Except(adminRights).ShouldBeEmpty();
        mailList.Except(adminRights).ShouldBeEmpty();
    }

    [Test]
    public void AdviceSkill_IsAReadOnlyQueryWithTheAdviseEffectInTheInboxArea()
    {
        var skill = Seeded(AdviceSkill);

        skill.GetProperty("category").GetString().ShouldBe("Query");
        skill.GetProperty("effect").GetString().ShouldBe("Advise");
        skill.GetProperty("area").GetString().ShouldBe("inbox");
        skill.GetProperty("alwaysOn").GetBoolean().ShouldBeFalse();
        skill.GetProperty("parameters").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public void AdviceSkill_HasAnAdvisesForEdgeToTheRuleCreation()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-relation-seeds.json")));

        document.RootElement.GetProperty("relations").EnumerateArray().ShouldContain(relation =>
            relation.GetProperty("skillAName").GetString() == AdviceSkill &&
            relation.GetProperty("skillBName").GetString() == CreateSkill &&
            relation.GetProperty("type").GetInt32() == AdvisesForType);
    }

    [Test]
    public void TheRuleCreation_IsSensitive_SoTheConfirmationTheAdviceAnnouncesIsReal()
    {
        var baselinePath = RepositoryRootLocator.RequireFile("Klacks.UnitTest", "Skills", "skill-risk-baseline.json");
        using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));

        baseline.RootElement.GetProperty(CreateSkill).GetString().ShouldBe(SensitiveClass);
        baseline.RootElement.GetProperty(AdviceSkill).GetString().ShouldBe("ReadOnly");
    }

    [Test]
    public void AdviceSkill_IsListedAsUntrustedOutput_BecauseSenderDomainsComeFromTheSender()
    {
        UntrustedSkillOutputs.Contains(AdviceSkill).ShouldBeTrue();
    }

    [Test]
    public void AdviceDescription_NamesNoOtherSkill()
    {
        var description = Seeded(AdviceSkill).GetProperty("description").GetString()!;

        description.ShouldNotContain(CreateSkill);
        description.ShouldNotContain(MailListSkill);
        description.ShouldNotContain("list_spam_rules");
        description.ShouldNotContain("_");
    }

    private static List<string?> RightsOf(string skillName) =>
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
