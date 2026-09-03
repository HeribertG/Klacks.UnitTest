// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Data.Seed;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class DemoOrderExportDecisionTests
{
    [Test]
    public void ShouldExportDemoOrders_ClientsWithoutShifts_ExportsTheOrderFile()
    {
        DemoDataSeedDecision.ShouldExportDemoOrders(seedDemoClients: true, seedDemoShiftsAndGroups: false).ShouldBeTrue();
    }

    [Test]
    public void ShouldExportDemoOrders_ShiftsAreSeeded_WritesNothing()
    {
        DemoDataSeedDecision.ShouldExportDemoOrders(seedDemoClients: true, seedDemoShiftsAndGroups: true).ShouldBeFalse();
    }

    [Test]
    public void ShouldExportDemoOrders_NoDemoDataAtAll_WritesNothing()
    {
        DemoDataSeedDecision.ShouldExportDemoOrders(seedDemoClients: false, seedDemoShiftsAndGroups: false).ShouldBeFalse();
        DemoDataSeedDecision.ShouldExportDemoOrders(seedDemoClients: false, seedDemoShiftsAndGroups: true).ShouldBeFalse();
    }
}
