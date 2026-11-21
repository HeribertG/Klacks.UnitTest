// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Domain.Services.Update;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Update;

[TestFixture]
public class UpdaterCapabilityEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    private static string Report(DateTime reportedAtUtc, params string[] capabilities)
    {
        var list = string.Join(",", capabilities.Select(capability => $"\"{capability}\""));
        return "{\"updaterVersion\":\"1.0.79\",\"mode\":\"Docker\",\"capabilities\":[" + list + "],\"reportedAtUtc\":\""
            + reportedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}";
    }

    [Test]
    public void FreshReportWithCapability_IsSupported()
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(Report(Now.AddMinutes(-3), "postgres-engine-upgrade/1"), Now)
            .ShouldBe(UpdaterCapabilityState.Supported);
    }

    [Test]
    public void UpdaterFormat_RoundTripsExactly()
    {
        const string report = "{\"updaterVersion\":\"1.0.79\",\"mode\":\"Native\",\"capabilities\":[\"postgres-engine-upgrade/1\"],\"reportedAtUtc\":\"2026-11-21T09:50:00.0000000Z\"}";

        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(report, Now).ShouldBe(UpdaterCapabilityState.Supported);
    }

    [Test]
    public void ExactlyTwentyMinutesOld_IsStillSupported()
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(Report(Now.AddMinutes(-20), "postgres-engine-upgrade/1"), Now)
            .ShouldBe(UpdaterCapabilityState.Supported);
    }

    [Test]
    public void OneSecondOverTwentyMinutes_IsStale()
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(
                Report(Now.AddMinutes(-20).AddSeconds(-1), "postgres-engine-upgrade/1"), Now)
            .ShouldBe(UpdaterCapabilityState.Stale);
    }

    [Test]
    public void FarFutureTimestamp_IsStale()
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(Report(Now.AddMinutes(21), "postgres-engine-upgrade/1"), Now)
            .ShouldBe(UpdaterCapabilityState.Stale);
    }

    [Test]
    public void SlightlyFutureTimestamp_IsSupported()
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(Report(Now.AddMinutes(1), "postgres-engine-upgrade/1"), Now)
            .ShouldBe(UpdaterCapabilityState.Supported);
    }

    [Test]
    public void FreshReportWithoutCapability_IsMissing()
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(Report(Now.AddMinutes(-1)), Now)
            .ShouldBe(UpdaterCapabilityState.Missing);
    }

    [Test]
    public void OtherCapabilityVersion_IsMissing()
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(Report(Now.AddMinutes(-1), "postgres-engine-upgrade/2"), Now)
            .ShouldBe(UpdaterCapabilityState.Missing);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{\"capabilities\":[\"postgres-engine-upgrade/1\"]}")]
    [TestCase("{\"capabilities\":[\"postgres-engine-upgrade/1\"],\"reportedAtUtc\":\"yesterday\"}")]
    [TestCase("{\"capabilities\":\"postgres-engine-upgrade/1\",\"reportedAtUtc\":\"2026-11-21T09:59:00.0000000Z\"}")]
    public void UnreadableReport_IsMissing(string? report)
    {
        UpdaterCapabilityEvaluator.EvaluateEngineUpgrade(report, Now).ShouldBe(UpdaterCapabilityState.Missing);
    }
}
