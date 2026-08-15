// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6g — the provably unsolvable run: six employees on holiday over the same two weeks leave
/// eleven for twelve assignments a day. It cannot be planned, and that is the point.
/// <para>
/// SETTING, not specification. The owner text asks for six employees carrying neither a keyword nor a
/// preference and names none, so the fixture takes the six consecutive ranks that carry neither —
/// MA-06 to MA-11 — and says so here rather than hiding the choice in a table.
/// </para>
/// <para>
/// What is asserted is NOT coverage. A run with eleven employees for twelve daily slots must leave
/// slots empty; demanding otherwise would ask the engine to invent capacity. What is asserted is that
/// the impossibility is paid for by the coverage rule alone: no absence day worked, no rest time cut,
/// no night followed by an early. An engine that bought the missing twelfth assignment by breaking one
/// of those would look better on rule 1 and be unusable in practice.
/// </para>
/// <para>
/// FOR THE REPORT: in THIS run the inherited scenario-5 assertions are not verdicts. S5-1 (full
/// coverage), S5-5 (carried-in packages continued), S5-7 (top-down hours) and S5-10 (rhythm and
/// cohorts) are all expected to be red, because six of seventeen employees are gone and none of those
/// four is reachable without them. Only S6-15 and
/// <see cref="S6g_TheInfeasibilityIsReportedRatherThanHidden"/> judge this run; everything else is
/// context. Counting the inherited reds here as scenario-6 findings would report the fixture as a
/// defect.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6gTests : Scenario6RunTestBase
{
    private const int MaxListedItems = 8;

    /// <inheritdoc />
    protected override bool TightestDayMustBeSolvable => false;

    [OneTimeSetUp]
    public void BuildAndRunTheUnsolvableScenario()
        => BuildGuardAndRun(
            Scenario6Fixture.BuildUnsolvableRun,
            Scenario6SpecConstants.RunGArtifactName,
            ValidateItIsProvablyUnsolvable);

    /// <summary>
    /// S6-15. No hard rule was sacrificed to hide the infeasibility.
    /// <para>
    /// THE RULE IS STATED HERE and not left to the reader. The hard rules that may never be traded for
    /// coverage are: (1) the absence block, (2) the contractual rest time, (3) the night-to-early ban,
    /// (4) the double booking, and (5) the shift blacklist. Coverage itself is deliberately NOT on the
    /// list — it is the one thing this run cannot have, and the whole scenario is about which rule the
    /// engine spends when it runs out of people.
    /// </para>
    /// <para>
    /// Channel (5) is reported SEPARATELY because it is an inherited finding. The scenario-5 results of
    /// 2026-08-14 measured nine blacklisted night shifts in a run with nobody absent at all: the
    /// blacklist is a hard veto in the auction only and the evolution tears it. A blacklist entry here
    /// is therefore that engine gap and not a scenario-6 one, and the report must not count it twice.
    /// </para>
    /// </summary>
    [Test]
    public void S6_15_NoHardRuleWasSacrificedToHideTheInfeasibility()
    {
        var sacrificed = new List<string>();

        if (Absences.Violations.Count > 0)
        {
            sacrificed.Add(
                $"absence block ({Count(Absences.Violations.Count)}): "
                + Scenario6Diagnostics.DescribeViolations(Absences.Violations));
        }

        if (Metrics.Legality.RestViolations.Count > 0)
        {
            sacrificed.Add($"contractual rest time ({Count(Metrics.Legality.RestViolations.Count)})");
        }

        if (Metrics.Legality.NightToEarlyViolations.Count > 0)
        {
            sacrificed.Add($"night-to-early ban ({Count(Metrics.Legality.NightToEarlyViolations.Count)})");
        }

        if (Metrics.Coverage.DoubleBookings.Count > 0)
        {
            sacrificed.Add($"double booking ({Count(Metrics.Coverage.DoubleBookings.Count)})");
        }

        var blacklist = Metrics.Preferences.BlacklistViolations;
        if (blacklist.Count > 0)
        {
            sacrificed.Add(
                $"shift blacklist ({Count(blacklist.Count)}) — INHERITED: scenario-5 finding S5-2 measured this same "
                + "gap with nobody absent, so it is an engine defect of the blacklist path and not a cost this run "
                + "paid for its infeasibility; do not count it twice");
        }

        TestContext.Out.WriteLine(
            $"S6-15: {Count(Metrics.Coverage.UnfilledShifts.Count)} of "
            + $"{Count(Metrics.Coverage.TotalRequiredShifts)} demanded shifts stayed unstaffed, which is the "
            + "infeasibility being reported rather than hidden");

        sacrificed.ShouldBeEmpty(
            "S6-15: this run cannot be planned — eleven available employees for twelve daily assignments — and the "
            + "only acceptable answer is to leave slots empty. Coverage is therefore NOT asserted here; every other "
            + "hard rule is. The rules that may never be spent for coverage: the absence block, the contractual "
            + "rest time, the night-to-early ban, the double booking and the shift blacklist. An engine that buys "
            + "the twelfth assignment by breaking one of them scores better on rule 1 and produces a plan nobody can "
            + "work. Sacrificed: " + string.Join(" | ", sacrificed.Take(MaxListedItems)));
    }

    /// <summary>
    /// The infeasibility has to be VISIBLE. A run that quietly staffed everything would mean either
    /// the fixture is not unsolvable after all or the measurement is not looking, and both would make
    /// S6-15 an assertion about an empty set.
    /// </summary>
    [Test]
    public void S6g_TheInfeasibilityIsReportedRatherThanHidden()
    {
        Metrics.Coverage.UnfilledShifts.Count.ShouldBeGreaterThan(
            0,
            "S6g: with six of seventeen employees absent, eleven remain for twelve assignments a day over fourteen "
            + "days — at least fourteen slots cannot be staffed by anybody. A fully staffed plan would mean an "
            + "absence day was worked, and S6-15 would then be judging a plan that solved an unsolvable problem.");
    }

    /// <summary>
    /// Proves the run really is unsolvable before anything is concluded from it: the six employees are
    /// absent over the same window, none of them carries a keyword or a preference, and the days
    /// inside the window genuinely demand more assignments than there are employees left.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    private static IReadOnlyList<string> ValidateItIsProvablyUnsolvable(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();

        foreach (var employee in Scenario6SpecConstants.UnsolvableRunEmployees)
        {
            problems.AddRange(Scenario6FixtureGuard.ValidateTwoWeekWindow(definition, employee));

            if (definition.ScheduleCommands?.CommandsOn(employee, Scenario6SpecConstants.HolidayFrom).Count > 0)
            {
                problems.Add(
                    $"'{employee}' carries a keyword command; the owner text asks for six employees WITHOUT "
                    + "keywords or preferences, so that the run measures capacity and nothing else");
            }

            if (definition.ShiftPreferences?.Preferences.Any(p => string.Equals(
                    p.AgentId, employee, StringComparison.Ordinal)) == true)
            {
                problems.Add(
                    $"'{employee}' carries a shift preference; the owner text asks for six employees WITHOUT "
                    + "keywords or preferences");
            }
        }

        var availability = Analysis.AbsenceAnalyzer.BuildAvailability(definition);
        var tightest = availability.TightestDay;
        if (tightest is null || tightest.AvailableEmployees >= tightest.RequiredAssignments)
        {
            problems.Add(
                "the tightest day still has at least as many available employees as it demands assignments, so this "
                + "run is not provably unsolvable and S6-15 would be asserting about an ordinary run: "
                + Scenario6Diagnostics.DescribeTightestDay(tightest));
        }

        return problems;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
