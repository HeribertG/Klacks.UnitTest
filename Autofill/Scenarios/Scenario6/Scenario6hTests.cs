// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6h — S6a reached through a replanning run. The specification asks whether the recovery entry
/// point sees absences the way the wizard does.
/// <para>
/// It does, and the code says why: <c>WizardHardConstraintBuilder.cs:46-48</c> passes
/// <c>replanFrom</c> to the locked-work and existing-work queries and deliberately NOT to the blocker
/// query, so absences block on both sides of the cut. This run is what turns that reading into a
/// measurement.
/// </para>
/// <para>
/// The gap this run is actually built to find is a different one. A frozen prefix is lifted from
/// <c>Work</c> alone (<c>:180-186</c>): a day inside the prefix that carries ONLY an absence produces
/// no locked token and is held by the blocker list alone. If those two ever disagreed, a replanning
/// run and a full run would differ exactly there — which is why the prefix is asserted byte for byte
/// AND the absence assertions of the base class run against the replanned plan as well.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; runs the main scenario and then replans it. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6hTests : Scenario6RunTestBase
{
    private const int MaxListedDifferences = 5;

    private static readonly DateOnly ReplanFrom = Scenario6SpecConstants.ReplanFrom;

    private CoreScenario? _basePlan;

    [OneTimeSetUp]
    public void FreezeTheHeadAndReplanTheTail()
    {
        var baseDefinition = Scenario6Fixture.BuildMainRun();
        var baseProblems = new List<string>(Scenario6FixtureGuard.Validate(baseDefinition));
        baseProblems.AddRange(Scenario6FixtureGuard.ValidateTwoWeekWindow(
            baseDefinition, Scenario6SpecConstants.HolidayEmployee));
        if (baseProblems.Count > 0)
        {
            RecordFixtureProblems(baseProblems);
            return;
        }

        _basePlan = TokenEvolutionLoop.Create().Run(baseDefinition.Context, baseDefinition.Config);
        var frozenHead = FrozenPrefix.BuildLockedWorks(_basePlan, ReplanFrom);

        TestContext.Out.WriteLine(
            $"S6h froze {frozenHead.Count.ToString(CultureInfo.InvariantCulture)} assignment(s) before "
            + $"{ReplanFrom:yyyy-MM-dd} out of a base plan of "
            + $"{_basePlan.Tokens.Count.ToString(CultureInfo.InvariantCulture)}. The replan date lies INSIDE the "
            + "holiday window, so the frozen prefix already contains absence days that carry no work at all — the "
            + "case in which the frozen prefix and the blocker list are the only thing holding the day.");

        BuildGuardAndRun(
            () => Scenario6Fixture.BuildReplanRun(frozenHead),
            Scenario6SpecConstants.RunHArtifactName,
            definition => Scenario6FixtureGuard.ValidateTwoWeekWindow(
                definition, Scenario6SpecConstants.HolidayEmployee));
    }

    /// <summary>
    /// The frozen head must survive the replan byte for byte, and every assignment before the replan
    /// date must enter the genome as a locked token.
    /// </summary>
    [Test]
    public void S6h_ThePrefixStaysUntouched()
    {
        _basePlan.ShouldNotBeNull("the base plan of the replanning run was not built");
        var before = AssignmentsBefore(_basePlan!, ReplanFrom);
        var after = AssignmentsBefore(Run.Plan, ReplanFrom);

        after.SetEquals(before).ShouldBeTrue(
            $"S6h: frozen-prefix replanning must not change a single assignment before {ReplanFrom:yyyy-MM-dd}. Base "
            + $"holds {before.Count.ToString(CultureInfo.InvariantCulture)}, the replan holds "
            + $"{after.Count.ToString(CultureInfo.InvariantCulture)}; only in the base: "
            + $"{string.Join("; ", before.Except(after).Take(MaxListedDifferences))}, only in the replan: "
            + string.Join("; ", after.Except(before).Take(MaxListedDifferences)));

        Run.Plan.Tokens.Where(t => t.Date < ReplanFrom).ShouldAllBe(
            t => t.IsLocked,
            "S6h: every frozen assignment must enter the genome as a locked token");
    }

    /// <summary>
    /// The absence must hold on BOTH sides of the replan cut. The blocker query is the one query the
    /// production builder does not narrow to the replan date, and this is the assertion that proves the
    /// consequence rather than quoting it.
    /// </summary>
    [Test]
    public void S6h_TheAbsenceBlocksOnBothSidesOfTheReplanCut()
    {
        var employee = Scenario6SpecConstants.HolidayEmployee;
        var absentDays = Definition.Absences.DaysOf(employee).ToHashSet();

        var beforeCut = Run.Plan.Tokens
            .Where(t => t.Date < ReplanFrom && t.AgentId == employee && absentDays.Contains(t.Date))
            .ToList();
        var afterCut = Run.Plan.Tokens
            .Where(t => t.Date >= ReplanFrom && t.AgentId == employee && absentDays.Contains(t.Date))
            .ToList();

        TestContext.Out.WriteLine(
            $"S6h: the holiday of {employee} covers "
            + $"{absentDays.Count(d => d < ReplanFrom).ToString(CultureInfo.InvariantCulture)} day(s) inside the "
            + "frozen prefix and "
            + $"{absentDays.Count(d => d >= ReplanFrom).ToString(CultureInfo.InvariantCulture)} day(s) in the "
            + "replanned tail");

        (beforeCut.Count + afterCut.Count).ShouldBe(
            0,
            "S6h: WizardHardConstraintBuilder.cs:46-48 hands replanFrom to the locked-work and existing-work "
            + "queries and deliberately NOT to the blocker query, so an absence blocks on both sides of the cut. A "
            + "violation before the cut would additionally mean the frozen prefix carried work onto an absence day: "
            + "the prefix is lifted from Work alone (:180-186), so an absence-only day produces no locked token and "
            + "is held by the blocker list by itself. "
            + $"Before the cut: {beforeCut.Count.ToString(CultureInfo.InvariantCulture)}, after it: "
            + afterCut.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static HashSet<string> AssignmentsBefore(CoreScenario plan, DateOnly limit)
        => plan.Tokens
            .Where(t => t.Date < limit)
            .Select(t => $"{t.AgentId}|{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.StartAt:O}")
            .ToHashSet(StringComparer.Ordinal);
}
