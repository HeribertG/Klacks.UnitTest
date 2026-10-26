// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds seal_shift and its preview provider over a given sealing service for tests that are not about the group an
/// order without a group is placed in: by default the placement resolver derives no group (reason NoGroupDerived), so an
/// order without a group is refused exactly as before the derivation existed.
/// </summary>

using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.TestHelpers;

internal static class SealShiftSkills
{
    public const string NoGroupDerived = "no group is named after the customer's city or state and no group carries coordinates";

    public static IOrderGroupPlacementResolver NoPlacement()
    {
        var placement = Substitute.For<IOrderGroupPlacementResolver>();
        placement.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new OrderGroupPlacement(null, NoGroupDerived));
        return placement;
    }

    public static SealShiftSkill Skill(IOrderSealingService sealing, IOrderGroupPlacementResolver? placement = null) =>
        new(new SealShiftTargetResolver(sealing, placement ?? NoPlacement()), sealing);

    public static SealShiftConfirmationPreviewProvider Preview(
        IOrderSealingService sealing, IOrderGroupPlacementResolver? placement = null) =>
        new(new SealShiftTargetResolver(sealing, placement ?? NoPlacement()));
}
