// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Seed-backed check of what a read-only personal access token can reach over MCP: every enabled seed
/// skill (core definitions, settings readers and feature plugins) is run through the real
/// SkillRiskClassifier and McpReadModeToolPolicy. Stubbed descriptors would be circular here, because
/// the ReadOnly class hangs on the seeded category. The effect guard is the independent check: a skill a Read
/// token may call must not be seeded as Mutate, unless it is listed in ReviewedMutateSeedsAllowedUnderRead
/// after somebody has read it.
/// </summary>

using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;
using Klacks.UnitTest.Infrastructure.Skills;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpReadModeToolPolicySeedTests
{
    private const int MinimumReadModeSeededSkills = 50;

    private static readonly string[] KnownWriters =
    [
        "create_employee",
        "update_client",
        "delete_client",
        "add_client_note",
        "send_message"
    ];

    private static readonly IReadOnlyDictionary<string, string> ReviewedMutateSeedsAllowedUnderRead =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rollback_my_last_change"] = "only looks up the inverse of the last execution and returns it as a proposal",
            ["start_guided_tour"] = "UiAction that only opens the onboarding overlay in the browser; never exposed over MCP",
        };

    private SkillRiskClassifier _classifier = null!;
    private McpReadModeToolPolicy _policy = null!;

    [SetUp]
    public void Setup()
    {
        _classifier = new SkillRiskClassifier();
        _policy = new McpReadModeToolPolicy(_classifier);
    }

    [Test]
    public void ReadMode_AllowsOnlyReadOnlySeedSkills()
    {
        var allowed = SkillSeedCatalog.EnabledSkills()
            .Select(SkillSeedCatalog.ToDescriptor)
            .Where(descriptor => _policy.IsAllowed(descriptor, PersonalAccessTokenAccessMode.Read))
            .ToList();

        allowed.Count.ShouldBeGreaterThan(MinimumReadModeSeededSkills);
        allowed.Where(descriptor => _classifier.Classify(descriptor) != SkillRiskClass.ReadOnly)
            .Select(descriptor => descriptor.Name)
            .ShouldBeEmpty();
        allowed.Select(descriptor => descriptor.Name).ShouldNotContain(AutonomyDefaults.ConfirmPendingActionSkillName);
        allowed.Select(descriptor => descriptor.Name).ShouldNotContain(PlanSkillDefaults.CreatePlanSkillName);
    }

    [Test]
    public void ReadMode_AllowsNoUnreviewedMutateSeedSkill()
    {
        var unreviewed = SkillSeedCatalog.EnabledSkills()
            .Where(skill => skill.Effect == SkillEffect.Mutate)
            .Where(skill => _policy.IsAllowed(SkillSeedCatalog.ToDescriptor(skill), PersonalAccessTokenAccessMode.Read))
            .Select(skill => skill.Name)
            .Where(name => !ReviewedMutateSeedsAllowedUnderRead.ContainsKey(name))
            .ToList();

        unreviewed.ShouldBeEmpty(
            "A read-only token could call these Mutate-seeded skills. Read each one: if it persists anything, add it "
            + "to DraftPersistingReadOnlySkills; if it truly only reads, list it here with the reason.");
    }

    [Test]
    public void ReadMode_RefusesKnownSeededWriters()
    {
        var seeded = SkillSeedCatalog.EnabledSkills()
            .ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var writer in KnownWriters)
        {
            seeded.ShouldContainKey(writer);
            _policy.IsAllowed(SkillSeedCatalog.ToDescriptor(seeded[writer]), PersonalAccessTokenAccessMode.Read)
                .ShouldBeFalse(writer);
        }
    }

    [Test]
    public void WriteMode_AllowsEverySeedSkill()
    {
        SkillSeedCatalog.EnabledSkills()
            .Select(SkillSeedCatalog.ToDescriptor)
            .Where(descriptor => !_policy.IsAllowed(descriptor, PersonalAccessTokenAccessMode.Write))
            .Select(descriptor => descriptor.Name)
            .ShouldBeEmpty();
    }
}
