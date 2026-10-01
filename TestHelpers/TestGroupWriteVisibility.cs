// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Substitutes for the visibility guards of group writes. A bare substitute answers "not visible" and an
/// empty filter result, which would make every existing group-write test look like a hidden-group case, so
/// tests that are not about visibility use these permissive stand-ins.
/// </summary>

using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.TestHelpers;

internal static class TestGroupWriteVisibility
{
    public static IGroupVisibilityGuard UnrestrictedGroups()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllGroupsVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    public static IClientVisibilityGuard AllClientsVisible()
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Func<Guid, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyCollection<Guid>>().ToList()));
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Client>>(), Arg.Any<Func<Client, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyCollection<Client>>().ToList()));
        return guard;
    }

    /// <summary>
    /// Clients guard that hides exactly the given ids and passes every other id through.
    /// </summary>
    /// <param name="hiddenClientIds">Clients the caller may not see</param>
    public static IClientVisibilityGuard ClientsHidden(params Guid[] hiddenClientIds)
    {
        var hidden = hiddenClientIds.ToHashSet();
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(!hidden.Contains(ci.Arg<Guid>())));
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyCollection<Guid>>().All(id => !hidden.Contains(id))));
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Func<Guid, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyCollection<Guid>>().Where(id => !hidden.Contains(id)).ToList()));
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Client>>(), Arg.Any<Func<Client, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyCollection<Client>>().Where(c => !hidden.Contains(c.Id)).ToList()));
        return guard;
    }
}
