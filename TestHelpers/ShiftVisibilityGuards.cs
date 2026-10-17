// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shift visibility guard substitutes for tests that are not about group visibility: AllowAll behaves like an
/// unrestricted caller (every shift visible, every group change allowed), HideAll like a restricted caller who sees
/// none of the shifts involved, Hiding like a restricted caller who sees every shift except the given ones.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

internal static class ShiftVisibilityGuards
{
    public static IShiftVisibilityGuard AllowAll() => Build(visible: true);

    public static IShiftVisibilityGuard HideAll() => Build(visible: false);

    public static IShiftVisibilityGuard Hiding(params Guid[] hiddenShiftIds)
    {
        var hidden = hiddenShiftIds.ToHashSet();
        var guard = Substitute.For<IShiftVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => !hidden.Contains(call.ArgAt<Guid>(0)));
        guard.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>())
            .Returns(call => !hidden.Contains(call.ArgAt<Shift>(0).Id));
        guard.ExistsAndVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => !hidden.Contains(call.ArgAt<Guid>(0)));
        guard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0).Where(hidden.Contains).Cast<Guid?>().FirstOrDefault());
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)call.ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(id => !hidden.Contains(id))
                .ToHashSet());
        return guard;
    }

    private static IShiftVisibilityGuard Build(bool visible)
    {
        var guard = Substitute.For<IShiftVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(visible);
        guard.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>()).Returns(visible);
        guard.ExistsAndVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(visible);
        guard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => visible ? null : call.Arg<IReadOnlyCollection<Guid>>().Cast<Guid?>().FirstOrDefault());
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)(visible
                ? call.Arg<IReadOnlyCollection<Guid>>().ToHashSet()
                : new HashSet<Guid>()));
        return guard;
    }
}
