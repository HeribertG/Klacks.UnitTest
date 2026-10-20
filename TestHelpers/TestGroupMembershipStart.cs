// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the real group-membership start planner on top of a substitute employment-window reader, so handler and skill
/// tests exercise the actual start-date logic without a database. Persons not listed have no membership record. The
/// membership repository answers "no ended memberships" and reports every staged pull-forward as changed unless a test
/// says otherwise.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestGroupMembershipStart
{
    public static IGroupMembershipStartPlanner Planner(ICompanyClock clock, params (Guid ClientId, DateOnly EntryDate)[] entries) =>
        Planner(clock, Substitute.For<IGroupItemRepository>(), Substitute.For<IGroupRepository>(), entries);

    public static IGroupMembershipStartPlanner Planner(
        ICompanyClock clock,
        IGroupItemRepository groupItems,
        IGroupRepository groups,
        params (Guid ClientId, DateOnly EntryDate)[] entries) =>
        Build(clock, groupItems, groups, Substitute.For<IUnitOfWork>(),
            entries.Select(e => (e.ClientId, e.EntryDate, (DateOnly?)null)).ToArray());

    public static IGroupMembershipStartPlanner Planner(
        ICompanyClock clock,
        IGroupItemRepository groupItems,
        IGroupRepository groups,
        IUnitOfWork unitOfWork,
        params (Guid ClientId, DateOnly EntryDate)[] entries) =>
        Build(clock, groupItems, groups, unitOfWork,
            entries.Select(e => (e.ClientId, e.EntryDate, (DateOnly?)null)).ToArray());

    public static IGroupMembershipStartPlanner PlannerWithExitDates(
        ICompanyClock clock, params (Guid ClientId, DateOnly EntryDate, DateOnly? ExitDate)[] entries) =>
        Build(clock, Substitute.For<IGroupItemRepository>(), Substitute.For<IGroupRepository>(), Substitute.For<IUnitOfWork>(), entries);

    private static IGroupMembershipStartPlanner Build(
        ICompanyClock clock,
        IGroupItemRepository groupItems,
        IGroupRepository groups,
        IUnitOfWork unitOfWork,
        (Guid ClientId, DateOnly EntryDate, DateOnly? ExitDate)[] entries)
    {
        var windows = entries.ToDictionary(entry => entry.ClientId, entry => new MembershipWindow(entry.EntryDate, entry.ExitDate));
        var reader = Substitute.For<IMembershipWindowReader>();
        reader.GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(windows);
        groupItems.GetEndedMembershipsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid ClientId, Guid GroupId, DateTime ValidUntil)>());
        groupItems.PullForwardValidFromAsync(Arg.Any<IReadOnlyDictionary<Guid, DateTime>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyDictionary<Guid, DateTime>>().Count);
        return new GroupMembershipStartPlanner(reader, clock, groupItems, groups, unitOfWork);
    }
}
