// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the real group-membership start planner on top of a substitute employment-window reader, so handler and skill
/// tests exercise the actual start-date logic without a database. Persons not listed have no membership record.
/// </summary>

using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestGroupMembershipStart
{
    public static IGroupMembershipStartPlanner Planner(ICompanyClock clock, params (Guid ClientId, DateOnly EntryDate)[] entries)
    {
        var windows = entries.ToDictionary(entry => entry.ClientId, entry => new MembershipWindow(entry.EntryDate, null));
        var reader = Substitute.For<IMembershipWindowReader>();
        reader.GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(windows);
        return new GroupMembershipStartPlanner(reader, clock);
    }

    public static IGroupMembershipStartPlanner PlannerWithExitDates(
        ICompanyClock clock, params (Guid ClientId, DateOnly EntryDate, DateOnly? ExitDate)[] entries)
    {
        var windows = entries.ToDictionary(entry => entry.ClientId, entry => new MembershipWindow(entry.EntryDate, entry.ExitDate));
        var reader = Substitute.For<IMembershipWindowReader>();
        reader.GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(windows);
        return new GroupMembershipStartPlanner(reader, clock);
    }
}
