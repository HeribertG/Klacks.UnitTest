// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proof that the research sub-loop toolset is hard read-only: driven by the REAL SkillRiskClassifier
/// over descriptors whose categories/names mirror the actual seed, it asserts that concrete mutating
/// skills (Crud, Sensitive, Reversible) are absent while genuine read-only skills survive, and that the
/// research skill excludes itself (recursion guard). The ReadOnly-classified draft writers
/// (DraftPersistingReadOnlySkills) never reach the sub-loop on any channel, and on behalf of an MCP caller the
/// toolset is capped to what that caller could call over MCP directly (exposure + read-mode policy); the seed-wide
/// subset proof against the real policies lives in ReadOnlyToolsetFilterSeedTests.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Mcp;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ReadOnlyToolsetFilterTests
{
    private const string RunAnalysis = "run_analysis";
    private const string ListPersonalAccessTokens = "list_personal_access_tokens";

    private ReadOnlyToolsetFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        var classifier = new SkillRiskClassifier();
        _filter = new ReadOnlyToolsetFilter(
            classifier, new McpSkillExposurePolicy(classifier), new McpReadModeToolPolicy(classifier));
    }

    private static SkillDescriptor Descriptor(string name, SkillCategory category) =>
        new(name, $"{name} description", category,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            ImplementationType: null);

    private static List<SkillDescriptor> Candidates() =>
    [
        // Read-only (Query / Read) — must survive.
        Descriptor("check_absence_conflicts", SkillCategory.Query),
        Descriptor("get_plan_status", SkillCategory.Read),
        // Mutating / risky — must never survive.
        Descriptor("create_shift", SkillCategory.Crud),
        Descriptor("add_break", SkillCategory.Crud),
        Descriptor("delete_client", SkillCategory.Crud),        // Sensitive by name
        Descriptor("apply_company_rule", SkillCategory.Action), // Sensitive by name
        Descriptor("delete_work", SkillCategory.Crud),          // Reversible extra — still mutates
        // The research skill itself is read-only but must be excluded to break recursion.
        Descriptor(RunAnalysis, SkillCategory.Query)
    ];

    [Test]
    public void Filter_KeepsGenuineReadOnlySkills()
    {
        var result = _filter.Filter(Candidates(), RunAnalysis, externalAgentAccessMode: null)
            .Select(d => d.Name)
            .ToList();

        result.ShouldContain("check_absence_conflicts");
        result.ShouldContain("get_plan_status");
    }

    [Test]
    public void Filter_ExcludesEveryMutatingSkill()
    {
        var result = _filter.Filter(Candidates(), RunAnalysis, externalAgentAccessMode: null)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mutating in new[]
                 {
                     "create_shift", "add_break", "delete_client", "apply_company_rule", "delete_work"
                 })
        {
            result.ShouldNotContain(mutating,
                $"mutating skill '{mutating}' must never reach the read-only research sub-toolset");
        }
    }

    [Test]
    public void Filter_ExcludesTheResearchSkillItself_RecursionGuard()
    {
        var withGuard = _filter.Filter(Candidates(), RunAnalysis, externalAgentAccessMode: null).Select(d => d.Name).ToList();
        withGuard.ShouldNotContain(RunAnalysis);
    }

    [Test]
    public void Filter_WithoutExclusion_TreatsResearchSkillAsReadOnly()
    {
        // Proves the exclusion above is the recursion guard, not the risk class: run_analysis IS read-only.
        var withoutGuard = _filter.Filter(Candidates(), excludeSkillName: null, externalAgentAccessMode: null).Select(d => d.Name).ToList();
        withoutGuard.ShouldContain(RunAnalysis);
    }

    [Test]
    public void Filter_ExcludesEveryDraftPersistingSkill_EvenInTheChat()
    {
        var drafts = DraftPersistingReadOnlySkills.Names
            .Select(name => Descriptor(name, SkillCategory.Action))
            .ToList();

        var result = _filter.Filter(drafts, RunAnalysis, externalAgentAccessMode: null);

        result.ShouldBeEmpty();
    }

    [TestCase(PersonalAccessTokenAccessMode.Read)]
    [TestCase(PersonalAccessTokenAccessMode.Write)]
    public void Filter_ForAnMcpCaller_DropsSkillsMcpDoesNotExpose(PersonalAccessTokenAccessMode accessMode)
    {
        var candidates = new List<SkillDescriptor>
        {
            Descriptor("check_absence_conflicts", SkillCategory.Query),
            Descriptor(ListPersonalAccessTokens, SkillCategory.Query),
            Descriptor("search_in_list", SkillCategory.UI) with { ExecutionType = LlmExecutionTypes.UiAction }
        };

        var result = _filter.Filter(candidates, RunAnalysis, accessMode).Select(d => d.Name).ToList();

        result.ShouldBe(new[] { "check_absence_conflicts" });
    }

    [Test]
    public void Filter_InTheChat_KeepsTheChatOnlyReadSkills()
    {
        var candidates = new List<SkillDescriptor>
        {
            Descriptor(ListPersonalAccessTokens, SkillCategory.Query)
        };

        var result = _filter.Filter(candidates, RunAnalysis, externalAgentAccessMode: null).Select(d => d.Name).ToList();

        result.ShouldContain(ListPersonalAccessTokens);
    }
}
