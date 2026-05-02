// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class ContainerWorkTimeCalculatorTests
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [Test]
    public void NoBreaks_ReturnsEnvelopeDuration()
    {
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(
            T(10), T(18), Array.Empty<(TimeOnly, TimeOnly)>());
        result.ShouldBe(8.0m);
    }

    [Test]
    public void OneUnpaidBreak_SubtractsDuration()
    {
        var breaks = new[] { (T(12), T(13)) };
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(T(10), T(18), breaks);
        result.ShouldBe(7.0m);
    }

    [Test]
    public void EnvelopeCrossesMidnight_ComputesCorrectly()
    {
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(
            T(22), T(6), Array.Empty<(TimeOnly, TimeOnly)>());
        result.ShouldBe(8.0m);
    }

    [Test]
    public void EnvelopeCrossesMidnight_UnpaidBreakAfterMidnight()
    {
        var breaks = new[] { (T(0), T(0, 30)) };
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(T(22), T(6), breaks);
        result.ShouldBe(7.5m);
    }

    [Test]
    public void BreakCrossesMidnight_WithinEnvelope()
    {
        var breaks = new[] { (T(23, 30), T(0, 15)) };
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(T(22), T(6), breaks);
        result.ShouldBe(7.25m);
    }

    [Test]
    public void BreakOutsideEnvelope_IsIgnored()
    {
        var breaks = new[] { (T(10), T(11)) };
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(T(22), T(6), breaks);
        result.ShouldBe(8.0m);
    }

    [Test]
    public void MultipleUnpaidBreaksExceedEnvelope_ClampedToZero()
    {
        var breaks = new[] { (T(10), T(14)), (T(14), T(18)), (T(10), T(18)) };
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(T(10), T(18), breaks);
        result.ShouldBe(0.0m);
    }

    [Test]
    public void StartEqualsEnd_MeansFullDay()
    {
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(
            T(0), T(0), Array.Empty<(TimeOnly, TimeOnly)>());
        result.ShouldBe(24.0m);
    }

    [Test]
    public void StartEqualsEnd_MinusUnpaidBreak()
    {
        var breaks = new[] { (T(12), T(13)) };
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(T(0), T(0), breaks);
        result.ShouldBe(23.0m);
    }

    [Test]
    public void ZeroLengthBreak_ContributesZero()
    {
        var breaks = new[] { (T(12), T(12)) };
        var result = ContainerWorkTimeCalculator.CalculatePaidHours(T(10), T(18), breaks);
        result.ShouldBe(8.0m);
    }
}
