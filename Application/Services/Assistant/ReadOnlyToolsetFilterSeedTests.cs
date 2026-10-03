// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Seed-backed guards for the research sub-loop toolset (run_analysis) against the real SkillRiskClassifier and
/// the real MCP policies:
/// (a) on behalf of an MCP caller the sub-loop never sees more than that caller could call over MCP directly -
/// under Read a subset of the read-mode set, under Write a subset of the exposed set; on no channel a draft writer.
/// (b) every SkillRiskClassifier.ReadOnlyExtras entry is either a known draft writer (DraftPersistingReadOnlySkills)
/// or on the reviewed list below. ReadOnlyExtras is the one place where a skill is declared ReadOnly against its
/// category or name, so a new entry has to be placed explicitly - otherwise this test fails.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Mcp;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.Infrastructure.Skills;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ReadOnlyToolsetFilterSeedTests
{
    private const string RunAnalysis = "run_analysis";
    private const int MinimumSubLoopSeedSkills = 50;

    private static readonly IReadOnlyDictionary<string, string> ReviewedNonPersistingReadOnlyExtras =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["find_customer_candidates"] = "lists candidate customers only",
            ["find_split_shift_candidates"] = "lists candidate shifts only",
            ["start_guided_tour"] = "UiAction that only opens the onboarding overlay in the browser",
            ["rollback_my_last_change"] = "only looks up the inverse of the last execution and returns it as a proposal",
            ["read_messages"] = "queries stored messages",
            ["list_messaging_providers"] = "queries provider rows",
            ["diagnose_messaging_setup"] = "reads provider rows, stored messages and vendor GET endpoints; never registers a webhook",
        };

    private SkillRiskClassifier _classifier = null!;
    private McpSkillExposurePolicy _exposurePolicy = null!;
    private McpReadModeToolPolicy _readModePolicy = null!;
    private ReadOnlyToolsetFilter _filter = null!;
    private List<SkillDescriptor> _seedDescriptors = null!;

    [SetUp]
    public void Setup()
    {
        _classifier = new SkillRiskClassifier();
        _exposurePolicy = new McpSkillExposurePolicy(_classifier);
        _readModePolicy = new McpReadModeToolPolicy(_classifier);
        _filter = new ReadOnlyToolsetFilter(_classifier, _exposurePolicy, _readModePolicy);
        _seedDescriptors = SkillSeedCatalog.EnabledSkills().Select(SkillSeedCatalog.ToDescriptor).ToList();
    }

    [Test]
    public void McpReadCaller_SubLoopToolset_IsSubsetOfTheReadModeSet()
    {
        var subLoop = _filter.Filter(_seedDescriptors, RunAnalysis, PersonalAccessTokenAccessMode.Read);

        subLoop.Count.ShouldBeGreaterThan(MinimumSubLoopSeedSkills);
        subLoop.Where(descriptor => !_exposurePolicy.IsExposed(descriptor)
                                    || !_readModePolicy.IsAllowed(descriptor, PersonalAccessTokenAccessMode.Read))
            .Select(descriptor => descriptor.Name)
            .ShouldBeEmpty();
    }

    [Test]
    public void McpWriteCaller_SubLoopToolset_IsSubsetOfTheExposedSet()
    {
        _filter.Filter(_seedDescriptors, RunAnalysis, PersonalAccessTokenAccessMode.Write)
            .Where(descriptor => !_exposurePolicy.IsExposed(descriptor))
            .Select(descriptor => descriptor.Name)
            .ShouldBeEmpty();
    }

    [TestCase(null)]
    [TestCase(PersonalAccessTokenAccessMode.Read)]
    [TestCase(PersonalAccessTokenAccessMode.Write)]
    public void SubLoopToolset_NeverHoldsADraftWriterOrConfirmPendingAction(PersonalAccessTokenAccessMode? accessMode)
    {
        var names = _filter.Filter(_seedDescriptors, RunAnalysis, accessMode)
            .Select(descriptor => descriptor.Name)
            .ToList();

        names.Where(DraftPersistingReadOnlySkills.Contains).ShouldBeEmpty();
        names.ShouldNotContain(AutonomyDefaults.ConfirmPendingActionSkillName);
        names.ShouldNotContain(RunAnalysis);
    }

    [Test]
    public void McpCaller_SubLoopToolset_NeverListsPersonalAccessTokens()
    {
        foreach (var accessMode in Enum.GetValues<PersonalAccessTokenAccessMode>())
        {
            _filter.Filter(_seedDescriptors, RunAnalysis, accessMode)
                .Select(descriptor => descriptor.Name)
                .ShouldNotContain("list_personal_access_tokens", accessMode.ToString());
        }
    }

    [Test]
    public void EveryReadOnlyExtra_IsEitherADraftWriterOrReviewedAsNonPersisting()
    {
        var unplaced = SkillRiskClassifier.ReadOnlyExtras
            .Where(name => !DraftPersistingReadOnlySkills.Contains(name))
            .Where(name => !ReviewedNonPersistingReadOnlyExtras.ContainsKey(name))
            .ToList();

        unplaced.ShouldBeEmpty(
            "These ReadOnlyExtras entries are unplaced. Read each skill: if it persists anything, add it to "
            + "DraftPersistingReadOnlySkills; if it truly only reads, list it in ReviewedNonPersistingReadOnlyExtras.");
    }

    [Test]
    public void DraftWritersAndReviewedReads_AreDisjoint_AndAllInReadOnlyExtras()
    {
        DraftPersistingReadOnlySkills.Names
            .Where(ReviewedNonPersistingReadOnlyExtras.ContainsKey)
            .ShouldBeEmpty();
        DraftPersistingReadOnlySkills.Names
            .Concat(ReviewedNonPersistingReadOnlyExtras.Keys)
            .Where(name => !SkillRiskClassifier.ReadOnlyExtras.Contains(name))
            .ShouldBeEmpty();
    }
}
