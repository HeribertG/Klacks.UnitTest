// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Substitutes for IBorrowedAgentFilter. A bare substitute answers an empty agent list, which would start every run
/// without agents, so tests that are not about borrowed people use the pass-through stand-in.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestBorrowedAgents
{
    public static IBorrowedAgentFilter None() => Removing();

    /// <summary>Filter that drops exactly the given agents and keeps the order of the rest.</summary>
    /// <param name="borrowedAgentIds">Agents that count as only borrowed into the planning group</param>
    public static IBorrowedAgentFilter Removing(params Guid[] borrowedAgentIds)
    {
        var filter = Substitute.For<IBorrowedAgentFilter>();
        filter.RemoveBorrowedAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<Guid>)call.ArgAt<IReadOnlyList<Guid>>(0)
                .Where(agentId => !borrowedAgentIds.Contains(agentId))
                .ToList());
        return filter;
    }
}
