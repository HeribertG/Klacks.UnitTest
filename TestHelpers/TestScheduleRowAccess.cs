// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the real ScheduleRowAccessGuard over a test's client guard, so by-id schedule handler tests keep their
/// client-visibility setup and get the scenario rule on top: every scenario visible by default, or one hidden token.
/// </summary>

using Klacks.Api.Application.Services.Groups;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestScheduleRowAccess
{
    public static IScheduleRowAccessGuard Over(IClientVisibilityGuard clientGuard)
        => new ScheduleRowAccessGuard(clientGuard, TestScenarioTokenAccess.AllVisible());

    /// <param name="clientGuard">Client visibility of the test</param>
    /// <param name="hiddenToken">Scenario token the caller may not see</param>
    public static IScheduleRowAccessGuard Hiding(IClientVisibilityGuard clientGuard, Guid hiddenToken)
        => new ScheduleRowAccessGuard(clientGuard, TestScenarioTokenAccess.Hiding(hiddenToken));
}
