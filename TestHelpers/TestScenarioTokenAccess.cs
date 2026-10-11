// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Substitutes for IScenarioTokenAccessGuard. A bare substitute answers "not visible", which would turn every scenario
/// token into a hidden one, so tests that are not about scenario visibility use the permissive stand-in.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

internal static class TestScenarioTokenAccess
{
    public static IScenarioTokenAccessGuard AllVisible()
    {
        var guard = Substitute.For<IScenarioTokenAccessGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    /// <summary>
    /// Guard that hides exactly the given token, refusing it like the real guard (KeyNotFoundException), and passes every other.
    /// </summary>
    /// <param name="hiddenToken">Scenario token the caller may not see</param>
    public static IScenarioTokenAccessGuard Hiding(Guid hiddenToken)
    {
        var guard = Substitute.For<IScenarioTokenAccessGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid?>(0) != hiddenToken);
        guard.EnsureVisibleAsync(Arg.Is<Guid?>(t => t == hiddenToken), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new KeyNotFoundException($"Scenario with token {hiddenToken} not found"));
        return guard;
    }
}
