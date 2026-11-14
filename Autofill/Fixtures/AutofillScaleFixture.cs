// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The scale case of Wizard 1 measurements: 50 employees at 180 guaranteed hours, the early/late/night triple with ten
/// seats per kind and day, March 2026. It mirrors the size of the integration test WizardSeriesTests.Scale_FiftyClients
/// without a database.
/// </summary>
public static class AutofillScaleFixture
{
    public const int EmployeeCount = 50;

    public const int SeatsPerShift = 10;

    public const double GuaranteedHours = 180;

    public static readonly DateOnly PeriodFrom = new(2026, 3, 1);

    public static readonly DateOnly PeriodUntil = new(2026, 3, 31);

    /// <summary>Builds the scale case with the given genetic-algorithm size.</summary>
    /// <param name="populationSize">Scenarios per generation</param>
    /// <param name="maxGenerations">Generation cap; early stopping is pinned to it</param>
    public static AutofillScenarioDefinition Build(int populationSize, int maxGenerations)
        => new AutofillScenarioBuilder()
            .WithPeriod(PeriodFrom, PeriodUntil)
            .WithEmployees(EmployeeCount, GuaranteedHours)
            .WithSeatsPerShift(SeatsPerShift)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(populationSize, maxGenerations)
            .Build();
}
