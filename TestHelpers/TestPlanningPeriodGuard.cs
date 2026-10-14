// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Substitutes for IClientPlanningPeriodGuard: a permissive stand-in that finds no cell outside a planning period, and
/// one that names exactly the given employee/day cells as violations, for tests that are not about the guard itself.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestPlanningPeriodGuard
{
    private const string ViolationMessage = "outside the membership period";

    public static IClientPlanningPeriodGuard NoViolations() => Violating();

    public static IClientPlanningPeriodGuard Violating(params (Guid ClientId, DateOnly Date)[] outsideCells)
    {
        var guard = Substitute.For<IClientPlanningPeriodGuard>();
        guard.FindViolationsAsync(Arg.Any<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(), Arg.Any<CancellationToken>())
            .Returns(call => Matching(call.Arg<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(), outsideCells));
        guard.FindViolationsInGroupsAsync(
                Arg.Any<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Matching(call.Arg<IReadOnlyCollection<(Guid ClientId, DateOnly Date)>>(), outsideCells));
        return guard;
    }

    private static IReadOnlyList<PlanningPeriodViolation> Matching(
        IReadOnlyCollection<(Guid ClientId, DateOnly Date)> cells,
        (Guid ClientId, DateOnly Date)[] outsideCells)
        => cells
            .Where(outsideCells.Contains)
            .Select(cell => new PlanningPeriodViolation(cell.ClientId, cell.Date, ViolationMessage))
            .ToList();
}
