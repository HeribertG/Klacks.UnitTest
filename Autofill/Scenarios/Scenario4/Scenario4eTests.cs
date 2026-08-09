// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Run S4e — the second entry point. The specification names a recovery run here, but the recovery
/// engine repairs a single absence and reads its data through a stored function; it is neither a
/// replanning mechanism nor database-free. The productive second entry point is the frozen prefix:
/// the head of an existing plan enters the next run as immutable locked works and only the tail is
/// planned again. That is the path the wizard's ReplanFrom takes in production, and it is the one
/// scenario 4 exercises.
/// <para>
/// The run inherits all shared assertions, which therefore judge the replanned plan as a whole, and
/// adds the three questions only a replanning run can pose: the frozen head must survive byte for
/// byte, the seam at the replan date must be legal, and the replanning run must reproduce itself.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 4 run; runs the main scenario and then replans it. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4eTests : Scenario4RunTestBase
{
    private static readonly DateOnly ReplanFrom = Scenario4SpecConstants.ReplanFrom;

    private CoreScenario? _basePlan;

    [OneTimeSetUp]
    public void FreezeTheHeadAndReplanTheTail()
    {
        var baseDefinition = Scenario4CarryInFixture.BuildMainRun();
        var baseProblems = Scenario4FixtureGuard.Validate(baseDefinition);
        if (baseProblems.Count > 0)
        {
            RecordFixtureProblems(baseProblems);
            return;
        }

        _basePlan = TokenEvolutionLoop.Create().Run(baseDefinition.Context, baseDefinition.Config);
        var frozenHead = FrozenPrefix.BuildLockedWorks(_basePlan, ReplanFrom);

        TestContext.Out.WriteLine(
            $"S4e froze {frozenHead.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) before "
            + $"{ReplanFrom:yyyy-MM-dd} out of a base plan of "
            + $"{_basePlan.Tokens.Count.ToString(CultureInfo.InvariantCulture)}. The frozen head is sorted by date, "
            + "start time and agent with no tiebreak on the shift reference; with three parallel orders two entries "
            + "could only tie if one agent held two of them at once, which stage 0 rules out, and LINQ's ordering is "
            + "stable in any case.");

        BuildGuardAndRun(
            () => Scenario4CarryInFixture.BuildReplanRun(frozenHead), Scenario4SpecConstants.RunEArtifactName);
    }

    [Test]
    public void S4e_ThePrefixStaysUntouched()
    {
        _basePlan.ShouldNotBeNull("the base plan of the replanning run was not built");
        var before = AssignmentsBefore(_basePlan!, ReplanFrom);
        var after = AssignmentsBefore(Run.Plan, ReplanFrom);

        after.SetEquals(before).ShouldBeTrue(
            $"S4e: frozen-prefix replanning must not change a single assignment before {ReplanFrom:yyyy-MM-dd}, over "
            + $"all three orders. Base holds {before.Count.ToString(CultureInfo.InvariantCulture)}, the replan holds "
            + $"{after.Count.ToString(CultureInfo.InvariantCulture)}; only in the base: "
            + $"{string.Join("; ", before.Except(after).Take(MaxListedDifferences))}, only in the replan: "
            + $"{string.Join("; ", after.Except(before).Take(MaxListedDifferences))}");

        Run.Plan.Tokens.Where(t => t.Date < ReplanFrom).ShouldAllBe(
            t => t.IsLocked,
            "S4e: every frozen assignment must enter the genome as a locked token");
    }

    [Test]
    public void S4e_TheReplannedPlanIsCoveredAndLegal()
    {
        var (legality, total) = new TokenConstraintChecker()
            .CountViolationsSplit(Run.Plan, Definition.Context);

        var problems = new List<string>();
        if (legality != 0)
        {
            problems.Add(
                $"{legality.ToString(CultureInfo.InvariantCulture)} hard-rule violation(s), the seam at "
                + $"{ReplanFrom:yyyy-MM-dd} included");
        }

        if (total != 0)
        {
            problems.Add(
                $"{total.ToString(CultureInfo.InvariantCulture)} stage-0 violation(s) in total, so slots remain "
                + "undersupplied");
        }

        problems.ShouldBeEmpty(
            "S4e: the replanned plan must break no hard rule and leave no slot unstaffed. The frozen head takes part "
            + "in the rest-time, consecutive-day and collision checks at the seam, so a violation here is a real "
            + $"defect of the replanning path and not an artefact of the freeze. {Describe(problems)}");
    }

    private static HashSet<string> AssignmentsBefore(CoreScenario plan, DateOnly limit)
        => plan.Tokens
            .Where(t => t.Date < limit)
            .Select(t => $"{t.AgentId}|{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.StartAt:O}")
            .ToHashSet(StringComparer.Ordinal);

    private const int MaxListedDifferences = 5;
}
