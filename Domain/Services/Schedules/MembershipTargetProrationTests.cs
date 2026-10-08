// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class MembershipTargetProrationTests
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly Until = new(2026, 3, 31);

    [Test]
    public void ExitOnThe15th_Gives15Of31() =>
        MembershipTargetProration.FactorFor(new MembershipWindow(new DateOnly(2020, 1, 1), new DateOnly(2026, 3, 15)), From, Until)
            .ShouldBe(15m / 31m);

    [Test]
    public void EntryOnThe10th_Gives22Of31() =>
        MembershipTargetProration.FactorFor(new MembershipWindow(new DateOnly(2026, 3, 10), null), From, Until)
            .ShouldBe(22m / 31m);

    [Test]
    public void EntryAndExitInsideThePeriod_CountsBothBoundsInclusive() =>
        MembershipTargetProration.FactorFor(new MembershipWindow(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 19)), From, Until)
            .ShouldBe(10m / 31m);

    [Test]
    public void MembershipCoveringThePeriod_IsNotScaled() =>
        MembershipTargetProration.FactorFor(new MembershipWindow(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)), From, Until)
            .ShouldBeNull();

    [Test]
    public void NoMembershipRow_IsNotScaled() =>
        MembershipTargetProration.FactorFor(null, From, Until).ShouldBeNull();

    [Test]
    public void MembershipOutsideThePeriod_IsZero() =>
        MembershipTargetProration.FactorFor(new MembershipWindow(new DateOnly(2020, 1, 1), new DateOnly(2026, 2, 28)), From, Until)
            .ShouldBe(0m);
}
