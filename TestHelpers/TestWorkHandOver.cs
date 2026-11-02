// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The real hand-over over a recalculation that succeeds, for tests of the handlers around it.
/// </summary>

using Klacks.Api.Application.Services.Schedules;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestWorkHandOver
{
    /// <param name="cascadeService">Records the move of the container children</param>
    public static IWorkHandOverService Over(IContainerWorkCascadeService cascadeService)
    {
        var childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        childrenManager.RecalculateForNewOwnerAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>()).Returns(true);
        return new WorkHandOverService(cascadeService, childrenManager);
    }
}
