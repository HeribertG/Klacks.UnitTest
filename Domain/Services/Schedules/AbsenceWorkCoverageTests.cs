// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// When an absence takes a work away entirely.
/// </summary>

using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class AbsenceWorkCoverageTests
{
    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    [TestCase(0, 0, 23, 59, 8, 16, true)]
    [TestCase(0, 0, 0, 0, 8, 16, true)]
    [TestCase(0, 0, 23, 59, 22, 6, true)]
    [TestCase(7, 0, 17, 0, 8, 16, true)]
    [TestCase(8, 0, 16, 0, 8, 16, true)]
    [TestCase(14, 0, 18, 0, 8, 16, false)]
    [TestCase(9, 0, 12, 0, 8, 16, false)]
    [TestCase(20, 0, 23, 0, 22, 6, false)]
    public void CoversWork(int fromHour, int fromMinute, int toHour, int toMinute, int workStart, int workEnd, bool expected)
        => AbsenceWorkCoverage.CoversWork(T(fromHour, fromMinute), T(toHour, toMinute), T(workStart), T(workEnd)).ShouldBe(expected);
}
