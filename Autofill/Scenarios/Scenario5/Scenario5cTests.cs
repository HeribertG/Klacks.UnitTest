// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Run S5c — the second entry point. The specification names a replanning run over <c>ReplanFrom</c>;
/// the productive mechanism behind that is the frozen prefix, exactly as in scenario 4: the head of an
/// existing plan enters the next run as immutable locked works and only the tail is planned again.
/// <para>
/// The run inherits all shared assertions, which therefore judge the replanned plan as a whole, and
/// adds the three questions only a replanning run can pose: the frozen head must survive byte for
/// byte, the seam at the replan date must be legal, and the replanning run must reproduce itself.
/// </para>
/// <para>
/// One asymmetry of the engine matters here and is stated rather than assumed. A LOCKED assignment is
/// exempt from stage 0 (<c>Stage0HardConstraintChecker</c> returns early for locked tokens), so
/// anything the base plan already got wrong about a blacklist or a keyword is frozen into this run and
/// cannot be repaired — <c>TokenRepair</c> explicitly refuses to remove a locked token. A red
/// blacklist assertion in S5c that is already red in S5a is therefore the SAME finding, not a second
/// one, and the report must not count it twice.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 5 run; runs the main scenario and then replans it. Select it by name.")]
[Category("Autofill")]
[Category("Scenario5")]
public class Scenario5cTests : Scenario5RunTestBase
{
    private const int MaxListedDifferences = 5;

    private static readonly DateOnly ReplanFrom = Scenario5SpecConstants.ReplanFrom;

    private CoreScenario? _basePlan;

    [OneTimeSetUp]
    public void FreezeTheHeadAndReplanTheTail()
    {
        var baseDefinition = Scenario5Fixture.BuildMainRun();
        var baseProblems = Scenario5FixtureGuard.Validate(baseDefinition);
        if (baseProblems.Count > 0)
        {
            RecordFixtureProblems(baseProblems);
            return;
        }

        _basePlan = TokenEvolutionLoop.Create().Run(baseDefinition.Context, baseDefinition.Config);
        var frozenHead = FrozenPrefix.BuildLockedWorks(_basePlan, ReplanFrom);

        TestContext.Out.WriteLine(
            $"S5c froze {frozenHead.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) before "
            + $"{ReplanFrom:yyyy-MM-dd} out of a base plan of "
            + $"{_basePlan.Tokens.Count.ToString(CultureInfo.InvariantCulture)}.");

        BuildGuardAndRun(
            () => Scenario5Fixture.BuildReplanRun(frozenHead), Scenario5SpecConstants.RunCArtifactName);
    }

    [Test]
    public void S5c_ThePrefixStaysUntouched()
    {
        _basePlan.ShouldNotBeNull("the base plan of the replanning run was not built");
        var before = AssignmentsBefore(_basePlan!, ReplanFrom);
        var after = AssignmentsBefore(Run.Plan, ReplanFrom);

        after.SetEquals(before).ShouldBeTrue(
            $"S5c: frozen-prefix replanning must not change a single assignment before {ReplanFrom:yyyy-MM-dd}, over "
            + $"all three orders and all four shifts. Base holds "
            + $"{before.Count.ToString(CultureInfo.InvariantCulture)}, the replan holds "
            + $"{after.Count.ToString(CultureInfo.InvariantCulture)}; only in the base: "
            + $"{string.Join("; ", before.Except(after).Take(MaxListedDifferences))}, only in the replan: "
            + string.Join("; ", after.Except(before).Take(MaxListedDifferences)));

        Run.Plan.Tokens.Where(t => t.Date < ReplanFrom).ShouldAllBe(
            t => t.IsLocked,
            "S5c: every frozen assignment must enter the genome as a locked token");
    }

    [Test]
    public void S5c_TheReplannedPlanIsCoveredAndLegal()
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
                + "undersupplied or a keyword is broken");
        }

        problems.ShouldBeEmpty(
            "S5c: the replanned plan must break no hard rule and leave no slot unstaffed. The frozen head takes part "
            + "in the rest-time, consecutive-day and collision checks at the seam, so a violation there is a real "
            + "defect of the replanning path and not an artefact of the freeze. Note on scope: the engine's own "
            + "stage-0 count includes keyword violations but knows nothing about the blacklist, which is not a "
            + $"ViolationKind — S5-2 measures that separately. {Describe(problems)}");
    }

    [Test]
    public void S5c_TheDayShiftSlotsSurviveTheFreeze()
    {
        var frozenDayShifts = Run.Plan.Tokens
            .Where(t => t.Date < ReplanFrom)
            .Count(t => AutofillShiftCatalog.IsDayShift(t.ShiftRefId));
        var replannedDayShifts = Run.Plan.Tokens
            .Where(t => t.Date >= ReplanFrom)
            .Count(t => AutofillShiftCatalog.IsDayShift(t.ShiftRefId));

        TestContext.Out.WriteLine(
            $"S5c day shifts: {frozenDayShifts.ToString(CultureInfo.InvariantCulture)} frozen, "
            + $"{replannedDayShifts.ToString(CultureInfo.InvariantCulture)} replanned");

        (frozenDayShifts + replannedDayShifts).ShouldBe(
            Scenario5SpecConstants.TotalDayShifts,
            "S5c: the day shift is distinguishable from the late shift by its shift reference id alone, and the "
            + "frozen prefix carries that id through the freeze. If the count came out short, the freeze would have "
            + "collapsed day shifts into late shifts — the one failure mode this scenario's whole modelling is "
            + "exposed to, and one no class-based check could see.");
    }

    private static HashSet<string> AssignmentsBefore(CoreScenario plan, DateOnly limit)
        => plan.Tokens
            .Where(t => t.Date < limit)
            .Select(t => $"{t.AgentId}|{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.StartAt:O}")
            .ToHashSet(StringComparer.Ordinal);
}
