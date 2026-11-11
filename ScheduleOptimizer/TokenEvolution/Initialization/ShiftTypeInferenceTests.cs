// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Every engine asks this one class what kind of shift it is looking at, and the answer decides who may
/// work it, what it is worth and how the plan is scored. The bands are Early 06:00-17:00, Late
/// 17:01-22:59, Night 23:00-05:59 (owner decision 2026-11-11; before: Early 06:00-14:59, Late
/// 15:00-22:59), and a shift is classified by its SPAN: the minutes per band are counted and the stronger
/// band (Night beats Late beats Early) wins unless its share is tolerated - at most 60 minutes AND less
/// than half of the shift (owner decision 2026-11-11, tolerance 60 minutes). The end is exclusive, so
/// 09:00-18:01 has exactly 60 late minutes and stays early, 09:00-18:02 has 61 and is late.
/// </summary>
[TestFixture]
public sealed class ShiftTypeInferenceTests
{
    [Test]
    public void FromSpan_ClassicEarlyShift_IsEarly()
    {
        Classify("06:00", "14:00").ShouldBe(ShiftTypeInference.EarlyIndex);
    }

    [Test]
    public void FromSpan_DayShiftEightToFour_IsEarly()
    {
        // Owner decision 2026-11-11: until then this shift reached into the late band from 15:00 on.
        Classify("08:00", "16:00").ShouldBe(ShiftTypeInference.EarlyIndex);
    }

    [Test]
    public void FromSpan_DayShiftReachingAFewMinutesIntoTheLateBand_IsEarly()
    {
        // Owner, 2026-11-11 (tolerance 60 minutes): 09:00-18:00 has 59 late minutes, which are tolerated.
        Classify("09:00", "18:00").ShouldBe(ShiftTypeInference.EarlyIndex);
    }

    [TestCase("18:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("18:01", ShiftTypeInference.EarlyIndex)]
    [TestCase("18:02", ShiftTypeInference.LateIndex)]
    public void FromSpan_LateMinutesAroundTheTolerance_SixtyAreTolerated(string end, int expected)
    {
        // The late band begins with the minute 17:01, so an end at 18:01 has exactly 60 late minutes.
        Classify("09:00", end).ShouldBe(expected);
    }

    [Test]
    public void FromSpan_ShiftStartingSixtyMinutesBeforeSix_IsEarly()
    {
        Classify("05:00", "13:00").ShouldBe(ShiftTypeInference.EarlyIndex);
    }

    [Test]
    public void FromSpan_ShiftStartingSixtyOneMinutesBeforeSix_IsNight()
    {
        Classify("04:59", "13:00").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [TestCase("15:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("15:01", ShiftTypeInference.EarlyIndex)]
    [TestCase("17:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("17:01", ShiftTypeInference.EarlyIndex)]
    [TestCase("17:02", ShiftTypeInference.EarlyIndex)]
    [TestCase("18:01", ShiftTypeInference.EarlyIndex)]
    [TestCase("18:02", ShiftTypeInference.LateIndex)]
    public void FromSpan_EndAroundTheLateBoundary_OnlyMoreThanSixtyLateMinutesTurnTheShiftLate(string end, int expected)
    {
        // 06:00 to 17:02 works the single late minute 17:01, which is tolerated; 06:00 to 18:02 works 61.
        Classify("06:00", end).ShouldBe(expected);
    }

    [TestCase("16:59", "22:00")]
    [TestCase("17:00", "22:00")]
    [TestCase("17:01", "22:00")]
    [TestCase("17:30", "18:30")]
    [TestCase("16:30", "17:45")]
    public void FromSpan_ShiftsLyingMostlyInTheLateBand_AreLate(string start, string end)
    {
        Classify(start, end).ShouldBe(ShiftTypeInference.LateIndex);
    }

    [Test]
    public void FromSpan_StartingAtFiveAndEndingAtOnePastFive_IsEarly()
    {
        // Its only minute is 17:00, which still belongs to the early band.
        Classify("17:00", "17:01").ShouldBe(ShiftTypeInference.EarlyIndex);
    }

    [TestCase("06:00", "14:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("07:00", "15:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("08:00", "16:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("09:00", "18:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("09:00", "18:01", ShiftTypeInference.EarlyIndex)]
    [TestCase("09:00", "18:02", ShiftTypeInference.LateIndex)]
    [TestCase("13:00", "19:00", ShiftTypeInference.LateIndex)]
    [TestCase("13:00", "21:00", ShiftTypeInference.LateIndex)]
    [TestCase("14:00", "22:00", ShiftTypeInference.LateIndex)]
    [TestCase("15:00", "23:00", ShiftTypeInference.LateIndex)]
    [TestCase("17:30", "18:30", ShiftTypeInference.LateIndex)]
    [TestCase("16:30", "17:45", ShiftTypeInference.LateIndex)]
    [TestCase("05:00", "13:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("04:59", "13:00", ShiftTypeInference.NightIndex)]
    [TestCase("14:00", "00:00", ShiftTypeInference.LateIndex)]
    [TestCase("16:00", "00:00", ShiftTypeInference.LateIndex)]
    [TestCase("15:00", "00:30", ShiftTypeInference.NightIndex)]
    [TestCase("22:30", "00:00", ShiftTypeInference.NightIndex)]
    [TestCase("22:15", "23:45", ShiftTypeInference.NightIndex)]
    [TestCase("22:00", "06:00", ShiftTypeInference.NightIndex)]
    [TestCase("23:00", "07:00", ShiftTypeInference.NightIndex)]
    [TestCase("07:00", "07:00", ShiftTypeInference.NightIndex)]
    [TestCase("00:00", "00:00", ShiftTypeInference.NightIndex)]
    public void FromSpan_TypicalShifts_FollowTheOwnerDecisionOf20261111(string start, string end, int expected)
    {
        Classify(start, end).ShouldBe(expected);
    }

    [Test]
    public void FromSpan_ClassicLateShift_IsLate()
    {
        Classify("15:00", "22:00").ShouldBe(ShiftTypeInference.LateIndex);
    }

    [Test]
    public void FromSpan_LateShiftReachingMoreThanSixtyMinutesIntoTheNightBand_IsNight()
    {
        Classify("15:00", "00:30").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [Test]
    public void FromSpan_LateShiftReachingAFewMinutesIntoTheNightBand_StaysLate()
    {
        Classify("15:00", "23:30").ShouldBe(ShiftTypeInference.LateIndex);
    }

    [Test]
    public void FromSpan_LateShiftEndingExactlyAtTheNightBoundary_StaysLate()
    {
        Classify("15:00", "23:00").ShouldBe(ShiftTypeInference.LateIndex);
    }

    [Test]
    public void FromSpan_ShiftAcrossMidnight_IsNight()
    {
        Classify("22:00", "06:00").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [Test]
    public void FromSpan_NightShiftRunningIntoTheMorning_StaysNight()
    {
        Classify("23:00", "07:00").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [Test]
    public void FromSpan_ShiftAcrossMidnightStartingInTheLateBand_IsNight()
    {
        Classify("21:00", "05:00").ShouldBe(ShiftTypeInference.NightIndex);
    }

    /// <summary>
    /// Equal bounds are a full 24 hour duty, not a zero-length span. Such a duty covers the whole
    /// night regardless of when it starts, so it is night work - owner ruling 2026-08-19. This
    /// replaces the former fallback to the start instant, under which an 07:00-07:00 duty counted
    /// as an early shift and lost its night premium in planning.
    /// </summary>
    [Test]
    public void FromSpan_EqualBounds_IsAFullDayAndThereforeNight()
    {
        Classify("08:00", "08:00").ShouldBe(ShiftTypeInference.NightIndex);
        Classify("07:00", "07:00").ShouldBe(ShiftTypeInference.NightIndex);
        Classify("04:00", "04:00").ShouldBe(ShiftTypeInference.NightIndex);
        Classify("00:00", "00:00").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [TestCase("00:00", ShiftTypeInference.NightIndex)]
    [TestCase("03:00", ShiftTypeInference.NightIndex)]
    [TestCase("05:59", ShiftTypeInference.NightIndex)]
    [TestCase("06:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("14:59", ShiftTypeInference.EarlyIndex)]
    [TestCase("15:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("16:59", ShiftTypeInference.EarlyIndex)]
    [TestCase("17:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("17:01", ShiftTypeInference.LateIndex)]
    [TestCase("22:59", ShiftTypeInference.LateIndex)]
    [TestCase("23:00", ShiftTypeInference.NightIndex)]
    public void FromStartTime_ClassifiesTheStartInstant(string start, int expected)
    {
        ShiftTypeInference.FromStartTime(TimeOnly.Parse(start)).ShouldBe(expected);
    }

    [Test]
    public void FromSpanString_UnparsableEnd_FallsBackToTheStartInstant()
    {
        ShiftTypeInference.FromSpanString("05:00", "not-a-time").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [Test]
    public void FromSpanString_UnparsableStart_IsEarly()
    {
        // No information at all: the least restrictive answer, as before.
        ShiftTypeInference.FromSpanString("nonsense", "14:00").ShouldBe(ShiftTypeInference.EarlyIndex);
    }

    private static int Classify(string start, string end)
        => ShiftTypeInference.FromSpan(TimeOnly.Parse(start), TimeOnly.Parse(end));
}
