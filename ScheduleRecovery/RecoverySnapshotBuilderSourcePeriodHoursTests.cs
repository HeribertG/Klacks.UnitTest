// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the period hours of a recovery snapshot built on a source scenario: the whole pay period comes from the
/// real plan, and only the covered employees' hours on the source scenario's own days are swapped for the scenario's.
/// </summary>

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.DTOs.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

[TestFixture]
public sealed class RecoverySnapshotBuilderSourcePeriodHoursTests
{
    private static readonly Guid Member = Guid.NewGuid();
    private static readonly Guid Borrowed = Guid.NewGuid();

    [Test]
    public void CoveredEmployee_GetsTheScenarioHoursOnTheOwnedDays_OnTopOfTheRealRestOfThePeriod()
    {
        var whole = Hours((Member, 120m, 160m), (Borrowed, 100m, 160m));
        var realOwned = Hours((Member, 16m, 0m), (Borrowed, 8m, 0m));
        var scenarioOwned = Hours((Member, 24m, 0m), (Borrowed, 40m, 0m));

        var merged = RecoverySnapshotBuilder.SwapOwnedPeriodHours(whole, realOwned, scenarioOwned, [Member]);

        merged[Member].Hours.ShouldBe(128m);
        merged[Member].GuaranteedHours.ShouldBe(160m);
        merged[Borrowed].Hours.ShouldBe(100m, "an employee the source scenario does not cover keeps the real figure");
    }

    [Test]
    public void CoveredEmployeeWithoutScenarioHours_LosesOnlyTheOwnedRealHours()
    {
        var whole = Hours((Member, 120m, 160m));
        var realOwned = Hours((Member, 16m, 0m));

        var merged = RecoverySnapshotBuilder.SwapOwnedPeriodHours(whole, realOwned, Hours(), [Member]);

        merged[Member].Hours.ShouldBe(104m);
    }

    private static Dictionary<Guid, PeriodHoursResource> Hours(params (Guid ClientId, decimal Hours, decimal Guaranteed)[] rows)
        => rows.ToDictionary(r => r.ClientId, r => new PeriodHoursResource { Hours = r.Hours, GuaranteedHours = r.Guaranteed });
}
