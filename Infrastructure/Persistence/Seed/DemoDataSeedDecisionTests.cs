// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Data.Seed;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class DemoDataSeedDecisionTests
{
    [Test]
    public void Decide_RegionFileConfiguredAndProfileTrue_SeedsFromProfile()
    {
        var (seedDemoData, source) = DemoDataSeedDecision.Decide(true, true, false);

        seedDemoData.ShouldBeTrue();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_RegionFileConfiguredAndProfileFalse_DoesNotSeed()
    {
        var (seedDemoData, source) = DemoDataSeedDecision.Decide(true, false, true);

        seedDemoData.ShouldBeFalse();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_RegionFileConfiguredAndProfileOmitted_DoesNotSeedEvenWhenLegacyFlagIsTrue()
    {
        var (seedDemoData, source) = DemoDataSeedDecision.Decide(true, null, true);

        seedDemoData.ShouldBeFalse();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_RegionFileConfiguredAndProfileTrue_IgnoresLegacyFlagFalse()
    {
        var (seedDemoData, source) = DemoDataSeedDecision.Decide(true, true, true);

        seedDemoData.ShouldBeTrue();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_NoRegionFileAndLegacyFlagTrue_SeedsFromLegacyConfiguration()
    {
        var (seedDemoData, source) = DemoDataSeedDecision.Decide(false, null, true);

        seedDemoData.ShouldBeTrue();
        source.ShouldBe(DemoDataSeedSource.LegacyFakeConfiguration);
    }

    [Test]
    public void Decide_NoRegionFileAndLegacyFlagFalse_DoesNotSeed()
    {
        var (seedDemoData, source) = DemoDataSeedDecision.Decide(false, null, false);

        seedDemoData.ShouldBeFalse();
        source.ShouldBe(DemoDataSeedSource.LegacyFakeConfiguration);
    }
}
