// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Run S5b — the control run: the identical fixture WITHOUT the shift preferences and WITHOUT the
/// keyword windows. It is the control group of the scenario, and the difference between it and S5a is
/// the whole measurement of what those two inputs did.
/// <para>
/// The shared assertions run against S5b as well, and two of them mean something different here, which
/// is exactly why they are worth running. S5-2 asks for no night shift for the two employees who are
/// blacklisted — in this run nothing forbids it, so a night shift here is CORRECT behaviour and the
/// measurement records how much the blacklist was actually worth. S5-4 asks for empty FREE windows,
/// and this run has no windows at all, so the metric is empty by construction. The report reads the
/// two runs side by side; neither number means anything alone.
/// </para>
/// <para>
/// Marked explicit for the same reason as S5a, and more so: this fixture also runs S5a, because a diff
/// needs both plans.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 5 run; runs the control run and the main run to diff them. Select it by name.")]
[Category("Autofill")]
[Category("Scenario5")]
public class Scenario5bTests : Scenario5RunTestBase
{
    private const int MaxListedItems = 12;

    private CoreScenario? _mainPlan;

    private AutofillScenarioDefinition? _mainDefinition;

    private AttributedAssignmentDiff? _diff;

    [OneTimeSetUp]
    public void RunTheControlAndDiffItAgainstTheMainRun()
    {
        BuildGuardAndRun(Scenario5Fixture.BuildControlRun, Scenario5SpecConstants.RunBArtifactName);
        if (!FixtureIsValid)
        {
            return;
        }

        _mainDefinition = Scenario5Fixture.BuildMainRun();
        var mainProblems = Scenario5FixtureGuard.Validate(_mainDefinition);
        if (mainProblems.Count > 0)
        {
            RecordFixtureProblems(mainProblems);
            return;
        }

        _mainPlan = TokenEvolutionLoop.Create().Run(_mainDefinition.Context, _mainDefinition.Config);
        _diff = PreferenceCommandAnalyzer.AttributeDiff(
            _mainPlan, _mainDefinition, Run.Plan, Definition);

        TestContext.Out.WriteLine("S5b attribution rule: " + _diff.AttributionRule);
        TestContext.Out.WriteLine("S5b diff against S5a: " + Scenario5Diagnostics.DescribeDiff(_diff));
    }

    /// <summary>
    /// S5-11. Every difference between the treated run and the control run has to trace back to a
    /// keyword window or a preference — those are the only two inputs that differ.
    /// <para>
    /// The rule is stated once in <see cref="PreferenceCommandAnalyzer.DiffAttributionRule"/> and is
    /// deliberately NOT widened if the count comes out above zero. A genetic algorithm propagates one
    /// constraint change across a whole month, so a rule generous enough to reach zero would explain
    /// every change and measure nothing; a nonzero count is a phase-C finding to be reported and
    /// judged, not a reason to loosen the rule.
    /// </para>
    /// </summary>
    [Test]
    public void S5_11_EveryDifferenceAgainstTheMainRunIsAttributable()
    {
        _diff.ShouldNotBeNull("the diff against the main run was not computed");

        var unexplained = _diff!.ChangedAssignments
            .Where(c => c.AttributedTo == DiffAttribution.Unexplained)
            .ToList();

        _diff.UnexplainedCount.ShouldBe(
            0,
            "S5-11: S5a and S5b differ in exactly two inputs — the shift preferences and the keyword windows — so "
            + "every slot the two plans staff differently must trace back to one of them, directly or as a knock-on "
            + "of a direct change. The attribution rule is fixed and stated in the artifact; it is never widened to "
            + $"reach zero. {Scenario5Diagnostics.DescribeDiff(_diff)}. Unexplained: "
            + Scenario5Diagnostics.DescribeUnexplained(unexplained.Take(MaxListedItems)));
    }

    /// <summary>
    /// The control run has to be worth something: if the two plans were identical, the preferences and
    /// the keyword windows had no measurable effect at all and every attribution above would be a
    /// statement about an empty set.
    /// </summary>
    [Test]
    public void S5_11_TheTwoInputsChangedThePlanAtAll()
    {
        _diff.ShouldNotBeNull("the diff against the main run was not computed");

        _diff!.ChangedAssignments.Count.ShouldBeGreaterThan(
            0,
            "S5b is the control group of the scenario. Two identical plans would mean the five keyword windows and "
            + "the twelve preference rows changed nothing measurable — with two FREE windows removing six employee "
            + "days and a night blacklist on two employees, that would itself be the finding, because at least the "
            + "FREE days cannot legally be staffed in the treated run.");

        TestContext.Out.WriteLine(
            $"S5b: {_diff.ChangedAssignments.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{Scenario5SpecConstants.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)} slots are staffed "
            + "differently once the preferences and the keyword windows are applied");
    }

    /// <summary>
    /// What the blacklist was actually worth, measured rather than asserted. The control run forbids
    /// nothing, so any night shift the two preference employees hold here is correct behaviour — and
    /// the count is the size of the effect S5-2 asks the treated run to remove completely.
    /// </summary>
    [Test]
    public void S5b_ReportsWhatTheBlacklistWouldHavePrevented()
    {
        var nights = Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => Scenario5SpecConstants.DayShiftEmployees.Contains(c.Employee, StringComparer.Ordinal))
            .ToList();

        foreach (var entry in nights)
        {
            TestContext.Out.WriteLine(
                $"S5b (no blacklist): {entry.Employee} holds "
                + $"{entry.Night.ToString(CultureInfo.InvariantCulture)} night shift(s), "
                + $"{entry.Early.ToString(CultureInfo.InvariantCulture)} early and "
                + $"{entry.Late.ToString(CultureInfo.InvariantCulture)} late");
        }

        TestContext.Out.WriteLine(
            "S5b day-shift shares without the preference: "
            + Scenario5Diagnostics.DescribeDayShiftShares(Metrics.DayShift.ShareByEmployee));

        nights.Count.ShouldBe(
            Scenario5SpecConstants.DayShiftEmployees.Count,
            "The control run must still contain both employees whose preferences the main run applies; otherwise the "
            + "comparison has nothing to compare.");
    }
}
