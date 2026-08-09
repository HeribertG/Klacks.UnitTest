// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// Proof of the frozen-prefix replanning mode (finding S3-F5): an existing L5 plan (MA-2 unbounded)
/// meets the master-data change of L1 (MA-2's NACHT-BEF expires 20.03.), the head of the plan is
/// frozen up to the change date and only the tail is replanned. The locality the L5-isolation test
/// cannot get from a free evolution must hold here by construction: nothing before the replan date
/// changes, the tail respects the new ban set, the whole plan stays covered and legal, and the
/// replanning run reproduces itself.
/// </summary>
[TestFixture]
public class Scenario3FrozenPrefixTests
{
    private static readonly DateOnly ReplanFrom = Scenario3SpecValues.Ma2NightBanFrom;

    private CoreScenario _basePlan = null!;
    private CoreScenario _replan = null!;
    private CoreScenario _replanRepeat = null!;
    private AutofillScenarioDefinition _replanDefinition = null!;

    [OneTimeSetUp]
    public void FreezeTheHeadAndReplanTheTail()
    {
        var baseDefinition = Scenario3EligibilityFixture.BuildL5();
        _basePlan = TokenEvolutionLoop.Create().Run(baseDefinition.Context, baseDefinition.Config);

        var frozenHead = FrozenPrefix.BuildLockedWorks(_basePlan, ReplanFrom);
        _replanDefinition = Scenario3EligibilityFixture.BuildL1(frozenHead);
        _replan = TokenEvolutionLoop.Create().Run(_replanDefinition.Context, _replanDefinition.Config);
        _replanRepeat = TokenEvolutionLoop.Create().Run(_replanDefinition.Context, _replanDefinition.Config);
    }

    [Test]
    public void PrefixStaysUntouched()
    {
        var before = AssignmentsBefore(_basePlan, ReplanFrom);
        var after = AssignmentsBefore(_replan, ReplanFrom);

        after.SetEquals(before).ShouldBeTrue(
            $"frozen-prefix replanning must not change a single assignment before {ReplanFrom:yyyy-MM-dd}: "
            + $"base holds {before.Count}, replan holds {after.Count}, "
            + $"only in base: {string.Join("; ", before.Except(after).Take(5))}, "
            + $"only in replan: {string.Join("; ", after.Except(before).Take(5))}");

        _replan.Tokens.Where(t => t.Date < ReplanFrom).ShouldAllBe(
            t => t.IsLocked,
            "every frozen assignment must enter the genome as a locked token");
    }

    [Test]
    public void TailRespectsTheNewExpiry()
    {
        var bans = _replanDefinition.Eligibility!.IneligibleAssignments;
        var offending = _replan.Tokens
            .Where(t => bans.Contains((t.AgentId, t.ShiftRefId, t.Date)))
            .Select(t => $"{t.AgentId} {t.Date:yyyy-MM-dd}")
            .ToList();

        offending.ShouldBeEmpty(
            "no assignment of the replanned plan may violate the new ban set - "
            + "MA-2 holds no night from the expiry on, MA-3/MA-4 hold none at all");
    }

    [Test]
    public void EveryShiftIsStaffedAndLegal()
    {
        var (legality, total) = new TokenConstraintChecker()
            .CountViolationsSplit(_replan, _replanDefinition.Context);

        legality.ShouldBe(0, "the replanned plan must not break a single hard rule, seam included");
        total.ShouldBe(0, "the replanned plan must staff every slot - no UnderSupply remains");
    }

    [Test]
    public void ReplanIsDeterministic()
    {
        var difference = PlanFingerprint.FirstDifference(
            PlanFingerprint.ForScenario(_replan),
            PlanFingerprint.ForScenario(_replanRepeat));

        difference.ShouldBeNull("two frozen-prefix replanning runs over the same input must be the same plan");
    }

    private static HashSet<string> AssignmentsBefore(CoreScenario plan, DateOnly limit)
        => plan.Tokens
            .Where(t => t.Date < limit)
            .Select(t => $"{t.AgentId}|{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.StartAt:O}")
            .ToHashSet(StringComparer.Ordinal);
}
