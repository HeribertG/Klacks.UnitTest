// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;

/// <summary>
/// Container sub-works (BitmapPlanningRules.IgnoredWorkIds) must not reach the rule context through the boundary
/// assignments either: an ignored boundary work behaves exactly as if it had never been loaded, while a boundary
/// assignment that still holds a counted work stays in the context. The carry-in is filtered at its source
/// (PlanningRuleDataReader leaves container sub-works out), so its segments carry no work ids here.
/// </summary>
[TestFixture]
public class BitmapRuleRuntimeIgnoredWorksTests
{
    private static readonly int[] Seeds = Enumerable.Range(1, 12).ToArray();

    [TestCaseSource(nameof(Seeds))]
    public void TryCreate_IgnoredBoundaryWorks_EvaluateLikeAnInputWithoutThem(int seed)
    {
        var probe = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules());
        var ignoredBoundary = probe.BoundaryAssignments!.Where((_, index) => index % 2 == 0).ToList();
        var ignored = ignoredBoundary.SelectMany(a => a.WorkIds).ToHashSet();
        var withIgnored = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules(), ignored);
        var withoutThem = withIgnored with
        {
            BoundaryAssignments = withIgnored.BoundaryAssignments!.Where(a => !a.WorkIds.Any(ignored.Contains)).ToList(),
            Rules = withIgnored.Rules! with { IgnoredWorkIds = null },
        };
        var bitmap = BitmapBuilder.Build(withIgnored);

        var actual = BitmapRuleRuntime.TryCreate(withIgnored)!.Evaluate(bitmap);
        var expected = BitmapRuleRuntime.TryCreate(withoutThem)!.Evaluate(bitmap);

        actual.ShouldBeEquivalentTo(expected);
    }

    [Test]
    public void TryCreate_IgnoredBoundaryWorks_ChangeTheEvaluationOnAtLeastOneSeed()
    {
        var changed = Seeds.Count(seed =>
        {
            var probe = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.HardRules());
            var ignored = probe.BoundaryAssignments!.SelectMany(a => a.WorkIds).ToHashSet();
            var withIgnored = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.HardRules(), ignored);
            var bitmap = BitmapBuilder.Build(probe);

            var counted = BitmapRuleRuntime.TryCreate(probe)!.Evaluate(bitmap);
            var skipped = BitmapRuleRuntime.TryCreate(withIgnored)!.Evaluate(bitmap);

            return !PlanningRuleBitmapFixture.HardExcess(counted).OrderBy(e => e.Key.ToString())
                .SequenceEqual(PlanningRuleBitmapFixture.HardExcess(skipped).OrderBy(e => e.Key.ToString()));
        });

        changed.ShouldBeGreaterThan(0, "the boundary works of the fixture must matter, otherwise the equivalence test proves nothing");
    }

    [Test]
    public void TryCreate_BoundaryAssignmentWithACountedWorkBesideAnIgnoredOne_StaysInTheContext()
    {
        var input = PlanningRuleBitmapFixture.Build(4, PlanningRuleBitmapFixture.HardRules());
        var boundary = input.BoundaryAssignments!.First(a => a.Symbol == CellSymbol.Night);
        var ignoredWorkId = Guid.NewGuid();
        var mixed = boundary with { WorkIds = [.. boundary.WorkIds, ignoredWorkId] };
        var withMixed = input with
        {
            BoundaryAssignments = input.BoundaryAssignments!.Select(a => ReferenceEquals(a, boundary) ? mixed : a).ToList(),
            Rules = input.Rules! with { IgnoredWorkIds = new HashSet<Guid> { ignoredWorkId } },
        };
        var bitmap = BitmapBuilder.Build(input);

        var actual = BitmapRuleRuntime.TryCreate(withMixed)!.Evaluate(bitmap);
        var expected = BitmapRuleRuntime.TryCreate(input)!.Evaluate(bitmap);

        actual.ShouldBeEquivalentTo(expected);
    }

    [Test]
    public void TryCreate_IgnoredWorkIdsMatchingNoBoundaryWork_KeepTheCarryInAndTheBoundary()
    {
        var input = PlanningRuleBitmapFixture.Build(6, PlanningRuleBitmapFixture.HardRules());
        var withUnrelated = input with { Rules = input.Rules! with { IgnoredWorkIds = new HashSet<Guid> { Guid.NewGuid() } } };
        var bitmap = BitmapBuilder.Build(input);

        var actual = BitmapRuleRuntime.TryCreate(withUnrelated)!.Evaluate(bitmap);
        var expected = BitmapRuleRuntime.TryCreate(input)!.Evaluate(bitmap);

        input.Rules!.CarryIn.ShouldNotBeEmpty();
        actual.ShouldBeEquivalentTo(expected);
    }
}
