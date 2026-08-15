// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6a — the main run: the scenario-5 fixture unchanged, plus MA-16 on holiday from 9 to 22
/// March. It is the only variant that differs from the control run S6-K in exactly ONE input, which
/// is what makes its comparison against that run attributable at all.
/// <para>
/// Marked explicit. One run of this size measured 522 s in phase C and the deterministic runner
/// executes it three times, so leaving it in the normal suite would turn a short suite into an hour
/// for every session sharing this working tree.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6aTests : Scenario6RunTestBase
{
    private const int MaxListedItems = 12;

    [OneTimeSetUp]
    public void BuildAndRunTheMainScenario()
        => BuildGuardAndRun(
            Scenario6Fixture.BuildMainRun,
            Scenario6SpecConstants.RunAArtifactName,
            definition => Scenario6FixtureGuard.ValidateTwoWeekWindow(
                definition, Scenario6SpecConstants.HolidayEmployee));

    /// <summary>
    /// S6-12. Every night shift that moved between the control run and this one has to trace back to
    /// the holiday, and nothing else changed between the two runs.
    /// <para>
    /// THE CONTROL RUN IS NOT WHAT THE OWNER TEXT ASSUMED, and the assertion says so rather than
    /// working around it. That text expected MA-16 to hold no night shift at all, because he is
    /// blacklisted from every one of them. The scenario-5 results of 2026-08-14 measured him holding
    /// three: the blacklist is a hard veto in the auction only, and the evolution tears it. Those
    /// three nights must therefore be reassigned by the holiday — that is explainable and expected —
    /// while everything beyond them needs a package-level justification the attribution rule does not
    /// grant. The inherited blacklist finding itself is S5-2 and must not be counted twice.
    /// </para>
    /// </summary>
    [Test]
    public void S6_12_EveryNightShiftThatMovedTracesBackToTheHoliday()
    {
        EnsureBaselineIsAvailable();

        var unexplained = DiffVsBaseline.NightDiffs
            .Where(d => d.AttributedTo == BaselineDiffAttribution.Unexplained)
            .ToList();

        TestContext.Out.WriteLine("S6-12 attribution rule: " + DiffVsBaseline.AttributionRule);
        TestContext.Out.WriteLine("S6-12 diff: " + Scenario6Diagnostics.DescribeDiff(DiffVsBaseline));

        DiffVsBaseline.UnexplainedNightCount.ShouldBe(
            0,
            "S6-12: S6a and the control run S6-K differ in exactly one input — the fourteen holiday rows of MA-16 — "
            + "so every night shift that changed hands must trace back to them. The rule is fixed, written into the "
            + "artifact and never widened to reach zero. Note on scope: the control run already contains night "
            + "shifts on blacklisted employees (scenario-5 finding S5-2, engine gap); those are inherited and are "
            + "not a scenario-6 finding, but the ones standing on holiday days MUST be reassigned here. Unexplained: "
            + string.Join(
                " | ",
                unexplained.Take(MaxListedItems).Select(d =>
                    $"{d.Employee} {d.BaselineNights.ToString(CultureInfo.InvariantCulture)}->"
                    + $"{d.CurrentNights.ToString(CultureInfo.InvariantCulture)} — {d.Explanation}")));
    }

    /// <summary>
    /// The control run has to be worth something. If the two plans agreed on every employee's night
    /// count, the holiday changed nothing measurable in the night distribution — which would itself be
    /// the finding, because the absent employee held three night shifts in the control run and cannot
    /// hold them here.
    /// </summary>
    [Test]
    public void S6a_TheHolidayChangedTheNightDistributionAtAll()
    {
        EnsureBaselineIsAvailable();

        var moved = DiffVsBaseline.NightDiffs.Count(d => d.Delta != 0);
        TestContext.Out.WriteLine(
            $"S6a: {moved.ToString(CultureInfo.InvariantCulture)} employee(s) hold a different number of night "
            + $"shifts than in the control run; the holiday freed "
            + $"{DiffVsBaseline.NightsRemovedByAbsence.ToString(CultureInfo.InvariantCulture)} of them");

        moved.ShouldBeGreaterThan(
            0,
            "S6a: the control run gave the now absent employee night shifts on days he is now blocked, so at least "
            + "his own count has to fall. Two identical night distributions would mean the holiday did not reach the "
            + "night plan at all, and every attribution above would be a statement about an empty set.");
    }
}
