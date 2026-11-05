// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the shared membership-day rule: inside the window and on both bounds a client is a member, outside it is not,
/// an open window has no end, and a client without a window (no membership row) is unrestricted.
/// </summary>

using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Domain.Models.Associations;

[TestFixture]
public class MembershipWindowsTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Entry = new(2026, 3, 2);
    private static readonly DateOnly Exit = new(2026, 3, 31);

    private static IReadOnlyDictionary<Guid, MembershipWindow> Windows(DateOnly? exit) =>
        new Dictionary<Guid, MembershipWindow> { [ClientId] = new(Entry, exit) };

    [Test]
    public void DayInsideTheWindow_IsAMemberDay()
    {
        MembershipWindows.IsMemberOn(Windows(Exit), ClientId, Entry.AddDays(10)).ShouldBeTrue();
    }

    [Test]
    public void EntryAndExitDay_AreMemberDays()
    {
        MembershipWindows.IsMemberOn(Windows(Exit), ClientId, Entry).ShouldBeTrue();
        MembershipWindows.IsMemberOn(Windows(Exit), ClientId, Exit).ShouldBeTrue();
    }

    [Test]
    public void DaysBeforeTheEntryAndAfterTheExit_AreNoMemberDays()
    {
        MembershipWindows.IsMemberOn(Windows(Exit), ClientId, Entry.AddDays(-1)).ShouldBeFalse();
        MembershipWindows.IsMemberOn(Windows(Exit), ClientId, Exit.AddDays(1)).ShouldBeFalse();
    }

    [Test]
    public void OpenWindow_HasNoEnd()
    {
        MembershipWindows.IsMemberOn(Windows(null), ClientId, Entry.AddYears(10)).ShouldBeTrue();
    }

    [Test]
    public void ClientWithoutAWindow_IsUnrestricted()
    {
        MembershipWindows.IsMemberOn(Windows(Exit), Guid.NewGuid(), Exit.AddYears(1)).ShouldBeTrue();
    }
}
