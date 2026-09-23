// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationDeadlineCalculator: default 60 minutes after the question, at the latest
/// 30 minutes before the affected shift starts, but never less than the 10-minute minimum answer window.
/// </summary>

using Klacks.Api.Domain.Services.Inbound;

namespace Klacks.UnitTest.Domain.Services.Inbound;

[TestFixture]
public class ClarificationDeadlineCalculatorTests
{
    private static readonly DateTime AskedAt = new(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc);

    [Test]
    public void WithoutShift_IsSixtyMinutesAfterAsking()
    {
        ClarificationDeadlineCalculator.Compute(AskedAt, null).ShouldBe(AskedAt.AddMinutes(60));
    }

    [Test]
    public void ShiftFarAway_KeepsTheDefault()
    {
        ClarificationDeadlineCalculator.Compute(AskedAt, AskedAt.AddHours(6)).ShouldBe(AskedAt.AddMinutes(60));
    }

    [Test]
    public void ShiftSoon_EndsThirtyMinutesBeforeShiftStart()
    {
        ClarificationDeadlineCalculator.Compute(AskedAt, AskedAt.AddMinutes(70)).ShouldBe(AskedAt.AddMinutes(40));
    }

    [Test]
    public void ShiftVerySoon_KeepsTheMinimumAnswerWindow()
    {
        ClarificationDeadlineCalculator.Compute(AskedAt, AskedAt.AddMinutes(20)).ShouldBe(AskedAt.AddMinutes(10));
    }

    [Test]
    public void ShiftAlreadyRunning_KeepsTheMinimumAnswerWindow()
    {
        ClarificationDeadlineCalculator.Compute(AskedAt, AskedAt.AddHours(-1)).ShouldBe(AskedAt.AddMinutes(10));
    }

    [Test]
    public void Result_IsUtc()
    {
        ClarificationDeadlineCalculator.Compute(AskedAt, AskedAt.AddMinutes(70)).Kind.ShouldBe(DateTimeKind.Utc);
    }
}
