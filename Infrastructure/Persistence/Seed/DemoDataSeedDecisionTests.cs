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
        var (seedDemoClients, seedDemoShiftsAndGroups, source) = DemoDataSeedDecision.Decide(true, true, null, false);

        seedDemoClients.ShouldBeTrue();
        seedDemoShiftsAndGroups.ShouldBeTrue();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_RegionFileConfiguredAndProfileFalse_DoesNotSeed()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, source) = DemoDataSeedDecision.Decide(true, false, null, true);

        seedDemoClients.ShouldBeFalse();
        seedDemoShiftsAndGroups.ShouldBeFalse();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_RegionFileConfiguredAndProfileOmitted_DoesNotSeedEvenWhenLegacyFlagIsTrue()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, source) = DemoDataSeedDecision.Decide(true, null, null, true);

        seedDemoClients.ShouldBeFalse();
        seedDemoShiftsAndGroups.ShouldBeFalse();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_RegionFileConfiguredAndProfileTrue_IgnoresLegacyFlagFalse()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, source) = DemoDataSeedDecision.Decide(true, true, null, true);

        seedDemoClients.ShouldBeTrue();
        seedDemoShiftsAndGroups.ShouldBeTrue();
        source.ShouldBe(DemoDataSeedSource.RegionSetupProfile);
    }

    [Test]
    public void Decide_NoRegionFileAndLegacyFlagTrue_SeedsFromLegacyConfiguration()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, source) = DemoDataSeedDecision.Decide(false, null, null, true);

        seedDemoClients.ShouldBeTrue();
        seedDemoShiftsAndGroups.ShouldBeTrue();
        source.ShouldBe(DemoDataSeedSource.LegacyFakeConfiguration);
    }

    [Test]
    public void Decide_NoRegionFileAndLegacyFlagFalse_DoesNotSeed()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, source) = DemoDataSeedDecision.Decide(false, null, null, false);

        seedDemoClients.ShouldBeFalse();
        seedDemoShiftsAndGroups.ShouldBeFalse();
        source.ShouldBe(DemoDataSeedSource.LegacyFakeConfiguration);
    }

    [Test]
    public void Decide_ProfileShiftsAndGroupsOmitted_FollowsSeedDemoData()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, _) = DemoDataSeedDecision.Decide(true, true, null, false);

        seedDemoClients.ShouldBeTrue();
        seedDemoShiftsAndGroups.ShouldBeTrue();
    }

    [Test]
    public void Decide_ProfileShiftsAndGroupsExplicitTrue_SeedsShiftsAndGroups()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, _) = DemoDataSeedDecision.Decide(true, true, true, false);

        seedDemoClients.ShouldBeTrue();
        seedDemoShiftsAndGroups.ShouldBeTrue();
    }

    [Test]
    public void Decide_ProfileShiftsAndGroupsExplicitFalse_SeedsClientsOnly()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, _) = DemoDataSeedDecision.Decide(true, true, false, false);

        seedDemoClients.ShouldBeTrue();
        seedDemoShiftsAndGroups.ShouldBeFalse();
    }

    [Test]
    public void Decide_ProfileShiftsAndGroupsTrueButSeedDemoDataFalse_StaysFalse()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, _) = DemoDataSeedDecision.Decide(true, false, true, false);

        seedDemoClients.ShouldBeFalse();
        seedDemoShiftsAndGroups.ShouldBeFalse();
    }

    [Test]
    public void Decide_NoRegionFileAndLegacyFlagTrue_AlwaysIncludesShiftsAndGroups()
    {
        var (seedDemoClients, seedDemoShiftsAndGroups, _) = DemoDataSeedDecision.Decide(false, null, false, true);

        seedDemoClients.ShouldBeTrue();
        seedDemoShiftsAndGroups.ShouldBeTrue();
    }
}
