// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The real WorkWriteGuardService over a given conflict checker, for handler tests that steer the hard-blocking check
/// through the checker substitute: the handler then refuses exactly as in production.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces.Schedules;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestWorkWriteGuard
{
    public static IWorkWriteGuard Over(IPreCommitConflictChecker conflictChecker)
        => new WorkWriteGuardService(
            Substitute.For<IShiftRepository>(),
            Substitute.For<IWorkRepository>(),
            conflictChecker);
}
