// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the one last-administrator rule: only an active account that holds the Admin role and is the only active
/// administrator counts, and the refusal text says what to do instead.
/// </summary>

using Klacks.Api.Application.Services.Authentication;

namespace Klacks.UnitTest.Application.Services.Authentication;

[TestFixture]
public class LastAdministratorRuleTests
{
    [TestCase(true, true, 1, true)]
    [TestCase(true, true, 2, false)]
    [TestCase(false, true, 1, false)]
    [TestCase(true, false, 1, false)]
    [TestCase(true, true, 0, true)]
    public void WouldRemoveLastActiveAdministrator_FollowsTheRule(bool holdsAdmin, bool active, int count, bool expected)
    {
        LastAdministratorRule.WouldRemoveLastActiveAdministrator(holdsAdmin, active, count).ShouldBe(expected);
    }

    [Test]
    public void RefusalMessage_IsReadableEnglishAndNamesTheRemedy()
    {
        LastAdministratorRule.RefusalMessage.ShouldContain("last active administrator");
        LastAdministratorRule.RefusalMessage.ShouldContain("Make another account an administrator first");
    }
}
