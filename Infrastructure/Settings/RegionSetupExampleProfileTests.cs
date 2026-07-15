// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Parses the shipped example region profile(s) under Klacks.Api/deploy/onprem/regions against the
/// current schema. The setup DTOs carry [JsonUnmappedMemberHandling(Disallow)], so a stale or misspelled
/// field in a shipped example would hard-fail a customer's first boot — this test catches that drift at
/// build time instead.
/// </summary>

using Klacks.Api.Infrastructure.Services.Settings;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionSetupExampleProfileTests
{
    [Test]
    public async Task ShippedGermanExampleProfile_ParsesAgainstCurrentSchema()
    {
        var path = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "Klacks.Api", "deploy", "onprem", "regions", "de.json"));
        File.Exists(path).ShouldBeTrue($"example profile not found at {path}");

        var profile = await RegionSetupFileReader.ReadProfileAsync(path);

        profile.Version.ShouldBe(1);
        profile.Region.ShouldBe("DE");
        var rollingCap = profile.Compliance.ShouldNotBeNull().PeriodCaps.ShouldNotBeNull().Single();
        rollingCap.WindowWeeks.ShouldBe(24);
        rollingCap.MaxAverageWeeklyHours.ShouldBe(48m);
        profile.Compliance!.Enforcement.ShouldNotBeNull().Rules.ShouldNotBeNull().RollingAverage.ShouldBe("warn");
    }
}
