// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Run S6e — the holiday that swallows a keyword window. MA-03's OnlyEarly window runs 18 to 24 March
/// and the holiday 16 to 29 March covers it completely, so the command can no longer narrow anything.
/// <para>
/// The expected behaviour is nothing at all. An Only* command never forces an assignment — it only
/// removes the classes it does not name — so a command over days that are already blocked constrains
/// an empty set. That is not an error state and must not become one: no exception while assembling or
/// running, no warning in the measurement, and above all no assignment inside the window that the
/// keyword could be blamed for.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 6 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario6")]
public class Scenario6eTests : Scenario6RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheKeywordCoveredScenario()
        => BuildGuardAndRun(
            Scenario6Fixture.BuildKeywordCoveredRun,
            Scenario6SpecConstants.RunEArtifactName,
            ValidateKeywordIsCovered);

    /// <summary>
    /// S6-13. The covered keyword window produces no exception, no warning and no assignment.
    /// </summary>
    [Test]
    public void S6_13_TheCoveredKeywordWindowProducesNothing()
    {
        var employee = Scenario6SpecConstants.KeywordHolidayEmployee;
        var problems = new List<string>();

        var window = Metrics.Keyword.Windows
            .FirstOrDefault(w => string.Equals(w.Employee, employee, StringComparison.Ordinal)
                                 && w.Keyword == ScheduleCommandKeyword.OnlyEarly);
        if (window is null)
        {
            problems.Add(
                $"the OnlyEarly window of '{employee}' is not reported at all; the measurement has to name it even "
                + "when it is empty, or the report cannot say whether it was harmless or simply lost");
        }
        else if (window.AssignmentsInWindow.Count > 0)
        {
            problems.Add(
                $"the covered window holds {window.AssignmentsInWindow.Count.ToString(CultureInfo.InvariantCulture)} "
                + "assignment(s): "
                + string.Join(", ", window.AssignmentsInWindow.Select(a => $"{a.Date:MM-dd} {a.SlotKind}")));
        }

        var violations = Metrics.Keyword.ScheduleCommandViolations
            .Where(v => string.Equals(v.Employee, employee, StringComparison.Ordinal))
            .ToList();
        if (violations.Count > 0)
        {
            problems.Add(
                $"{violations.Count.ToString(CultureInfo.InvariantCulture)} keyword violation(s) on an employee "
                + "whose window is entirely blocked");
        }

        var notes = Metrics.Notes
            .Where(n => n.Contains(employee, StringComparison.Ordinal))
            .ToList();
        if (notes.Count > 0)
        {
            problems.Add("the measurement carries a note about that employee: " + string.Join(" | ", notes));
        }

        problems.ShouldBeEmpty(
            "S6-13: a keyword window fully covered by a holiday is without effect, and being without effect is not "
            + "an error. An Only* command only removes the classes it does not name; it never forces an assignment, "
            + "so over blocked days it constrains an empty set. The run must therefore assemble and finish without "
            + "an exception, report the window as empty rather than dropping it, and produce neither a violation nor "
            + "a warning. " + Describe(problems));
    }

    /// <summary>
    /// Checks the premise of the run: the keyword window really does lie completely inside the
    /// holiday. If one day of it stuck out, the window would still constrain something and S6-13 would
    /// assert about a different situation than the one it describes.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    private static IReadOnlyList<string> ValidateKeywordIsCovered(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();
        var employee = Scenario6SpecConstants.KeywordHolidayEmployee;

        for (var date = Scenario6SpecConstants.KeywordWindowFrom;
             date <= Scenario6SpecConstants.KeywordWindowUntil;
             date = date.AddDays(1))
        {
            if (!definition.Absences.Covers(employee, date))
            {
                problems.Add(
                    $"the keyword window day {date:yyyy-MM-dd} of '{employee}' is not covered by the holiday, so the "
                    + "window is not gone and this run does not test what it claims to");
            }
        }

        var commands = definition.ScheduleCommands?.CommandsOn(
            employee, Scenario6SpecConstants.KeywordWindowFrom) ?? [];
        if (!commands.Any(c => c.Keyword == ScheduleCommandKeyword.OnlyEarly))
        {
            problems.Add(
                $"'{employee}' carries no OnlyEarly command on "
                + $"{Scenario6SpecConstants.KeywordWindowFrom:yyyy-MM-dd}; the inherited scenario-5 keyword windows "
                + "must be present, or there is no window for the holiday to swallow");
        }

        return problems;
    }
}
