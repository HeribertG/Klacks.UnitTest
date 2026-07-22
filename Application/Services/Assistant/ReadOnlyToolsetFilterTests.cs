// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proof that the research sub-loop toolset is hard read-only: driven by the REAL SkillRiskClassifier
/// over descriptors whose categories/names mirror the actual seed, it asserts that concrete mutating
/// skills (Crud, Sensitive, Reversible) are absent while genuine read-only skills survive, and that the
/// research skill excludes itself (recursion guard).
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.Meta;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ReadOnlyToolsetFilterTests
{
    private const string RunAnalysis = "run_analysis";

    private ReadOnlyToolsetFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _filter = new ReadOnlyToolsetFilter(new SkillRiskClassifier());
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
        var result = _filter.Filter(Candidates(), RunAnalysis)
            .Select(d => d.Name)
            .ToList();

        result.ShouldContain("check_absence_conflicts");
        result.ShouldContain("get_plan_status");
    }

    [Test]
    public void Filter_ExcludesEveryMutatingSkill()
    {
        var result = _filter.Filter(Candidates(), RunAnalysis)
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
        var withGuard = _filter.Filter(Candidates(), RunAnalysis).Select(d => d.Name).ToList();
        withGuard.ShouldNotContain(RunAnalysis);
    }

    [Test]
    public void Filter_WithoutExclusion_TreatsResearchSkillAsReadOnly()
    {
        // Proves the exclusion above is the recursion guard, not the risk class: run_analysis IS read-only.
        var withoutGuard = _filter.Filter(Candidates(), excludeSkillName: null).Select(d => d.Name).ToList();
        withoutGuard.ShouldContain(RunAnalysis);
    }
}
