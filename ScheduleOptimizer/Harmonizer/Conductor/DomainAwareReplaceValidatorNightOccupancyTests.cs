// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

/// <summary>
/// A night shift occupies the morning of the next day. When a swap hands a night to a row on the last day of its block,
/// that morning extends the block - the consecutive-day check must read the crossing from the INCOMING cell, not from
/// the free cell the swap replaces. Regression: Wizard 2/3 used to accept such swaps and left runs one day over the cap.
/// </summary>
[TestFixture]
public sealed class DomainAwareReplaceValidatorNightOccupancyTests
{
    [Test]
    public void NightEndingAFiveDayBlock_IsRejected()
    {
        // Row 0 works days 0-4; a night on day 5 makes day 5 the sixth day and the morning of day 6 the seventh.
        var bitmap = HarmonyTestPlans.Bitmap("EEEEE____", "_____N___");
        var validator = new DomainAwareReplaceValidator(null);

        var reason = validator.Diagnose(bitmap, new ReplaceMove(0, 1, 5));

        reason.ShouldNotBeNull();
        reason.ShouldContain("MaxConsecutiveDays");
    }

    [Test]
    public void NightEndingAFourDayBlock_IsAccepted()
    {
        var bitmap = HarmonyTestPlans.Bitmap("EEEE_____", "____N____");
        var validator = new DomainAwareReplaceValidator(null);

        validator.Diagnose(bitmap, new ReplaceMove(0, 1, 4)).ShouldBeNull();
    }

    [Test]
    public void CellInPlace_CountsTheMorningAfterANight()
    {
        var bitmap = HarmonyTestPlans.Bitmap("EEEEEN___");
        var validator = new DomainAwareReplaceValidator(null);

        validator.DiagnoseCellInPlace(bitmap, 0, 5).ShouldNotBeNull();
    }
}
