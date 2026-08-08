// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// Builds specification scenario 2 — the clean start of scenario 1 plus the carry-in packages of the
/// previous month — and checks the assembled fixture for consistency before anything is run.
/// <para>
/// The consistency check exists so that a broken setup is reported as a setup error and never as a
/// red rule assertion: a test that blames the algorithm for a fixture mistake is worse than no test.
/// It combines the shared <see cref="AutofillCarryInGuard"/> with the scenario-specific expectation
/// of the specification, namely that on 2026-03-01 exactly MA-1 (night), MA-2 (late) and MA-3 (early)
/// are still inside a package and cover the three shifts of that day, while MA-4 and MA-5 are free.
/// </para>
/// </summary>
public static class Scenario2CarryInFixture
{
    private static readonly (string AgentId, AutofillShiftKind Kind)[] ExpectedOpenPackages =
    [
        (Scenario2SpecValues.Ma1, Scenario2SpecValues.Ma1Kind),
        (Scenario2SpecValues.Ma2, Scenario2SpecValues.Ma2Kind),
        (Scenario2SpecValues.Ma3, Scenario2SpecValues.Ma3Kind),
    ];

    private static readonly string[] ExpectedFreeOnFirstDay =
    [
        Scenario2SpecValues.Ma4,
        Scenario2SpecValues.Ma5,
    ];

    /// <summary>
    /// Assembles scenario 2: period 2026-03-01..03-31, three shifts a day, five employees at 180
    /// guaranteed hours in list order, and the five previous-month packages of the specification table.
    /// </summary>
    public static AutofillScenarioDefinition Build()
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, AutofillSpecConstants.GuaranteedHours)
            .WithCarryIn(
                new AutofillCarryIn(
                    Scenario2SpecValues.Ma1,
                    Scenario2SpecValues.Ma1Kind,
                    Scenario2SpecValues.Ma1PackageStart,
                    Scenario2SpecValues.Ma1PackageEnd,
                    AutofillSpecConstants.MaxWorkDays,
                    Scenario2SpecValues.Ma1ExpectedFirstKind,
                    Scenario2SpecValues.Ma1RemainingDays),
                new AutofillCarryIn(
                    Scenario2SpecValues.Ma2,
                    Scenario2SpecValues.Ma2Kind,
                    Scenario2SpecValues.Ma2PackageStart,
                    Scenario2SpecValues.Ma2PackageEnd,
                    AutofillSpecConstants.MaxWorkDays,
                    Scenario2SpecValues.Ma2ExpectedFirstKind,
                    Scenario2SpecValues.Ma2RemainingDays),
                new AutofillCarryIn(
                    Scenario2SpecValues.Ma3,
                    Scenario2SpecValues.Ma3Kind,
                    Scenario2SpecValues.Ma3PackageStart,
                    Scenario2SpecValues.Ma3PackageEnd,
                    AutofillSpecConstants.MaxWorkDays,
                    Scenario2SpecValues.Ma3ExpectedFirstKind,
                    Scenario2SpecValues.Ma3RemainingDays),
                new AutofillCarryIn(
                    Scenario2SpecValues.Ma4,
                    Scenario2SpecValues.Ma4Kind,
                    Scenario2SpecValues.Ma4PackageStart,
                    Scenario2SpecValues.Ma4PackageEnd,
                    AutofillSpecConstants.MaxWorkDays,
                    Scenario2SpecValues.Ma4ExpectedFirstKind,
                    Scenario2SpecValues.ClosedPackageRemainingDays),
                new AutofillCarryIn(
                    Scenario2SpecValues.Ma5,
                    Scenario2SpecValues.Ma5Kind,
                    Scenario2SpecValues.Ma5PackageStart,
                    Scenario2SpecValues.Ma5PackageEnd,
                    AutofillSpecConstants.MaxWorkDays,
                    Scenario2SpecValues.Ma5ExpectedFirstKind,
                    Scenario2SpecValues.ClosedPackageRemainingDays))

            // WithCarryInHoursAsCurrentHours() is deliberately NOT called. The February hours must not
            // count as CoreAgent.CurrentHours against the March guaranteed hours: a non-zero opening
            // balance makes the fitness plan fewer in-period hours, which would move the hour and
            // fairness assertions (A6, A8) away from their scenario 1 counterparts and destroy the
            // comparison the analysis of phase D rests on. In production that balance is filled by a
            // separate mechanism, not by the boundary works.
            .Build();

    /// <summary>
    /// Returns one message per fixture problem; an empty result means the fixture matches the
    /// specification and the run may proceed.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    public static IReadOnlyList<string> Validate(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>(AutofillCarryInGuard.Validate(definition));
        var open = definition.OpenCarryIns;
        var openDescription = open.Count == 0
            ? "none"
            : string.Join(", ", open.Select(c => $"{c.AgentId}={c.Kind}"));

        foreach (var (agentId, kind) in ExpectedOpenPackages)
        {
            var match = open.FirstOrDefault(c => string.Equals(c.AgentId, agentId, StringComparison.Ordinal));
            if (match is null)
            {
                problems.Add(
                    $"{agentId} must still be inside a {kind} package on {definition.PeriodFrom:yyyy-MM-dd}, "
                    + $"but it is not among the open packages [{openDescription}].");
                continue;
            }

            if (match.Kind != kind)
            {
                problems.Add(
                    $"{agentId} must carry a {kind} package into {definition.PeriodFrom:yyyy-MM-dd}, but the fixture "
                    + $"declares {match.Kind}.");
            }
        }

        foreach (var agentId in ExpectedFreeOnFirstDay)
        {
            if (open.Any(c => string.Equals(c.AgentId, agentId, StringComparison.Ordinal)))
            {
                problems.Add(
                    $"{agentId} must be free on {definition.PeriodFrom:yyyy-MM-dd} because its previous-month package "
                    + $"is already closed, but it is listed as still open [{openDescription}].");
            }
        }

        if (open.Count != Scenario2SpecValues.OpenCarryInCount)
        {
            problems.Add(
                $"{Scenario2SpecValues.OpenCarryInCount} employees must be inside a package on "
                + $"{definition.PeriodFrom:yyyy-MM-dd} and cover its three shifts, but {open.Count} are "
                + $"[{openDescription}].");
        }

        var boundaryDays = definition.Context.BoundaryLockedWorks.Count;
        if (boundaryDays != Scenario2SpecValues.TotalCarryInDays)
        {
            problems.Add(
                $"The carry-in table describes {Scenario2SpecValues.TotalCarryInDays} previous-month work days "
                + $"(2 + 4 + 1 + 5 + 5), but the builder produced {boundaryDays.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (definition.CarryInHoursCountedAsCurrentHours)
        {
            problems.Add(
                "The February hours were handed to the engine as CoreAgent.CurrentHours. Scenario 2 requires them to "
                + "stay out of the hour account, otherwise its hour assertions are not comparable to scenario 1.");
        }

        return problems;
    }
}
