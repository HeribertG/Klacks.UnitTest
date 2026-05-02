// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ScheduleCommandKeywordMapperTests
{
    [TestCase("FREE", ScheduleCommandKeyword.Free)]
    [TestCase("-FREE", ScheduleCommandKeyword.NotFree)]
    [TestCase("EARLY", ScheduleCommandKeyword.OnlyEarly)]
    [TestCase("-EARLY", ScheduleCommandKeyword.NoEarly)]
    [TestCase("LATE", ScheduleCommandKeyword.OnlyLate)]
    [TestCase("-LATE", ScheduleCommandKeyword.NoLate)]
    [TestCase("NIGHT", ScheduleCommandKeyword.OnlyNight)]
    [TestCase("-NIGHT", ScheduleCommandKeyword.NoNight)]
    public void TryMap_ReturnsCorrectEnum(string input, ScheduleCommandKeyword expected)
    {
        ScheduleCommandKeywordMapper.TryMap(input, out var result).ShouldBeTrue();
        result.ShouldBe(expected);
    }

    [TestCase("free")]
    [TestCase(" FREE ")]
    public void TryMap_IsCaseInsensitiveAndTrims(string input)
    {
        ScheduleCommandKeywordMapper.TryMap(input, out var result).ShouldBeTrue();
        result.ShouldBe(ScheduleCommandKeyword.Free);
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("UNKNOWN")]
    [TestCase("FREEZE")]
    public void TryMap_ReturnsFalseForUnknown(string? input)
    {
        ScheduleCommandKeywordMapper.TryMap(input, out _).ShouldBeFalse();
    }
}
