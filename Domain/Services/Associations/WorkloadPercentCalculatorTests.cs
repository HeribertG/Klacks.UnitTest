// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the one workload-share rule both contract paths and the no-contract path scale absence days by:
/// guaranteed hours / full time x 100, full workload for a missing full-time basis or a ratio above the ceiling.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Associations;

namespace Klacks.UnitTest.Domain.Services.Associations;

[TestFixture]
public class WorkloadPercentCalculatorTests
{
    [Test]
    public void Resolve_PartTimeRatio_IsGuaranteedOverFullTime()
    {
        var percent = WorkloadPercentCalculator.Resolve(170m, 180m, out var reason);

        percent.ShouldBe(170m / 180m * MonthlyTargetHoursConstants.FullWorkloadPercent);
        reason.ShouldBeNull();
    }

    [Test]
    public void Resolve_ModestOverFullTime_PassesThrough()
    {
        var percent = WorkloadPercentCalculator.Resolve(198m, 180m, out var reason);

        percent.ShouldBe(110m);
        reason.ShouldBeNull();
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void Resolve_NoPositiveFullTime_FallsBackToFullWorkload(decimal fullTime)
    {
        var percent = WorkloadPercentCalculator.Resolve(170m, fullTime, out var reason);

        percent.ShouldBe(MonthlyTargetHoursConstants.FullWorkloadPercent);
        reason.ShouldBe(WorkloadPercentCalculator.NoPositiveFullTimeReason);
    }

    [Test]
    public void Resolve_RatioAboveCeiling_FallsBackToFullWorkload()
    {
        var percent = WorkloadPercentCalculator.Resolve(172.2m, 45m, out var reason);

        percent.ShouldBe(MonthlyTargetHoursConstants.FullWorkloadPercent);
        reason.ShouldBe(WorkloadPercentCalculator.CeilingExceededReason);
    }

    [Test]
    public void Resolve_ZeroGuaranteedHours_IsZeroPercentNotAFallback()
    {
        var percent = WorkloadPercentCalculator.Resolve(0m, 180m, out var reason);

        percent.ShouldBe(0m);
        reason.ShouldBeNull();
    }
}
