// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Every engine asks this one class what kind of shift it is looking at, and the answer decides who may
/// work it, what it is worth and how the plan is scored. The bands are Early 06:00-14:59, Late
/// 15:00-22:59, Night 23:00-05:59, and a shift takes the strongest band its SPAN touches - Night beats
/// Late beats Early - because a shift that runs into the night is night work whatever the roster says.
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
    public void FromSpan_EarlyShiftReachingIntoTheLateBand_IsLate()
    {
        // Most of it sits in the early band; the last hour does not, and that decides.
        Classify("08:00", "16:00").ShouldBe(ShiftTypeInference.LateIndex);
    }

    [Test]
    public void FromSpan_ShiftStartingBeforeSix_IsNight()
    {
        Classify("05:00", "13:00").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [Test]
    public void FromSpan_EndingExactlyAtTheLateBoundary_StaysEarly()
    {
        // The end is exclusive: the last worked minute is 14:59.
        Classify("06:00", "15:00").ShouldBe(ShiftTypeInference.EarlyIndex);
    }

    [Test]
    public void FromSpan_EndingOneMinutePastTheLateBoundary_IsLate()
    {
        Classify("06:00", "15:01").ShouldBe(ShiftTypeInference.LateIndex);
    }

    [Test]
    public void FromSpan_ClassicLateShift_IsLate()
    {
        Classify("15:00", "22:00").ShouldBe(ShiftTypeInference.LateIndex);
    }

    [Test]
    public void FromSpan_LateShiftReachingIntoTheNightBand_IsNight()
    {
        // Night is the strongest band, so it wins over late for the same reason late wins over early.
        Classify("15:00", "23:30").ShouldBe(ShiftTypeInference.NightIndex);
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

    [Test]
    public void FromSpan_ZeroLengthSpan_FallsBackToTheStartInstant()
    {
        Classify("08:00", "08:00").ShouldBe(ShiftTypeInference.EarlyIndex);
        Classify("04:00", "04:00").ShouldBe(ShiftTypeInference.NightIndex);
    }

    [TestCase("00:00", ShiftTypeInference.NightIndex)]
    [TestCase("03:00", ShiftTypeInference.NightIndex)]
    [TestCase("05:59", ShiftTypeInference.NightIndex)]
    [TestCase("06:00", ShiftTypeInference.EarlyIndex)]
    [TestCase("14:59", ShiftTypeInference.EarlyIndex)]
    [TestCase("15:00", ShiftTypeInference.LateIndex)]
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
