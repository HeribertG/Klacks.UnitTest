// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shift visibility guard substitutes for tests that are not about group visibility: AllowAll behaves like an
/// unrestricted caller (every shift visible, every group change allowed), HideAll like a restricted caller who sees
/// none of the shifts involved.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

internal static class ShiftVisibilityGuards
{
    public static IShiftVisibilityGuard AllowAll() => Build(visible: true);

    public static IShiftVisibilityGuard HideAll() => Build(visible: false);

    private static IShiftVisibilityGuard Build(bool visible)
    {
        var guard = Substitute.For<IShiftVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(visible);
        guard.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>()).Returns(visible);
        guard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => visible ? null : call.Arg<IReadOnlyCollection<Guid>>().Cast<Guid?>().FirstOrDefault());
        return guard;
    }
}
