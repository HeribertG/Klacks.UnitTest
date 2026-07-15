// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the K20 entity-import reconciliation: a row not yet known is inserted, a row whose
/// current live values still hash to what was last imported is updated to the new desired content, and
/// a row whose live values no longer match its stored hash (a customer edit) is left untouched.
/// </summary>

using Klacks.Api.Application.Services.Setup;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Setup;

[TestFixture]
public class EntityImportPlannerTests
{
    private const string KeyA = "region-setup:compliance.periodCaps:month:totalhours";
    private const string KeyB = "region-setup:compliance.periodCaps:year:totalhours";

    [Test]
    public void Plan_SourceKeyNotYetKnown_Inserts()
    {
        var desired = new List<EntityImportDesired<int>>
        {
            new(KeyA, "hash-a", 200),
        };

        var decisions = EntityImportPlanner.Plan(new Dictionary<string, bool>(), desired);

        decisions.Count.ShouldBe(1);
        decisions[0].Action.ShouldBe(EntityImportAction.Insert);
        decisions[0].SourceKey.ShouldBe(KeyA);
        decisions[0].ContentHash.ShouldBe("hash-a");
        decisions[0].Values.ShouldBe(200);
    }

    [Test]
    public void Plan_ExistingRowUnedited_Updates()
    {
        var desired = new List<EntityImportDesired<int>>
        {
            new(KeyA, "hash-a-new", 250),
        };
        var existingUnedited = new Dictionary<string, bool> { [KeyA] = true };

        var decisions = EntityImportPlanner.Plan(existingUnedited, desired);

        decisions.Count.ShouldBe(1);
        decisions[0].Action.ShouldBe(EntityImportAction.Update);
        decisions[0].ContentHash.ShouldBe("hash-a-new");
        decisions[0].Values.ShouldBe(250);
    }

    [Test]
    public void Plan_ExistingRowEdited_SkipsWithoutOverwriting()
    {
        var desired = new List<EntityImportDesired<int>>
        {
            new(KeyA, "hash-a-new", 250),
        };
        var existingUnedited = new Dictionary<string, bool> { [KeyA] = false };

        var decisions = EntityImportPlanner.Plan(existingUnedited, desired);

        decisions.Count.ShouldBe(1);
        decisions[0].Action.ShouldBe(EntityImportAction.SkipEdited);
    }

    [Test]
    public void Plan_MultipleDesiredRows_DecidesEachIndependently()
    {
        var desired = new List<EntityImportDesired<int>>
        {
            new(KeyA, "hash-a", 200),
            new(KeyB, "hash-b", 2000),
        };
        var existingUnedited = new Dictionary<string, bool> { [KeyA] = true };

        var decisions = EntityImportPlanner.Plan(existingUnedited, desired);

        decisions.Single(d => d.SourceKey == KeyA).Action.ShouldBe(EntityImportAction.Update);
        decisions.Single(d => d.SourceKey == KeyB).Action.ShouldBe(EntityImportAction.Insert);
    }
}
