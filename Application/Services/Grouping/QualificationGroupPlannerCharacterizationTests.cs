// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for QualificationGroupPlanner.Plan: the exact text and order of every warning (missing
/// master, unnamed, then per group in ordinal display-name order: ambiguous existing group and merged names, also for a
/// group skipped by the minimum), the reuse of the lowest-id existing group under its own untrimmed name, the ordinal
/// group order and the plan counters.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class QualificationGroupPlannerCharacterizationTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly IReadOnlyList<string> GermanFirst = ["de", "en", "fr", "it"];
    private static readonly Guid LowerGroupId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid HigherGroupId = new("00000000-0000-0000-0000-000000000002");

    [Test]
    public void Plan_ReportsEveryWarningVerbatim_InTheFixedOrder_AndReusesTheLowestIdGroupUnderItsOwnName()
    {
        var parentId = Guid.NewGuid();
        var forklift = Qualification("Staplerfahrer");
        var forkliftTwin = Qualification("Staplerfahrer");
        var firstAid = Qualification("Erste Hilfe");
        var crane = Qualification("Kranführer");
        var craneTwin = Qualification("Kranführer");
        var unnamed = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage() };
        var ghost = Qualification("Ghost");
        var removed = Qualification("Removed");
        removed.IsDeleted = true;

        var anna = Holder(forklift, firstAid, ghost, unnamed);
        var beat = Holder(forkliftTwin, firstAid, removed);
        var carl = Holder(crane, craneTwin);
        var dora = Holder();
        var groups = new List<Group>
        {
            new() { Id = HigherGroupId, Name = "Erste Hilfe", Parent = parentId },
            new() { Id = LowerGroupId, Name = "erste hilfe ", Parent = parentId }
        };

        var plan = QualificationGroupPlanner.Plan(
            [anna, beat, carl, dora],
            [forklift, forkliftTwin, firstAid, crane, craneTwin, unnamed, removed],
            groups,
            new QualificationGroupPlanContext(GermanFirst, Today, parentId, null, 2, true));

        plan.Warnings.ShouldBe(new[]
        {
            "2 client qualification(s) point to a qualification that no longer exists; they were ignored.",
            "1 client qualification(s) point to a qualification without a name in the installation language or a core language; they were ignored.",
            "Several groups named 'Erste Hilfe' already exist under the same parent; the first one (id 00000000-0000-0000-0000-000000000001) is reused.",
            "Several qualifications are named 'Kranführer' in the installation language; their holders share one group.",
            "Several qualifications are named 'Staplerfahrer' in the installation language; their holders share one group."
        });
        plan.TotalClients.ShouldBe(4);
        plan.ConsideredClients.ShouldBe(4);
        plan.SkippedAlreadyGroupedCount.ShouldBe(0);
        plan.ClientsWithoutQualificationCount.ShouldBe(1);

        plan.Groups.Count.ShouldBe(2);
        var reused = plan.Groups[0];
        reused.Name.ShouldBe("erste hilfe ");
        reused.Existed.ShouldBeTrue();
        reused.ExistingGroupId.ShouldBe(LowerGroupId);
        reused.QualificationIds.ShouldBe(new[] { firstAid.Id });
        reused.MemberClientIds.ShouldBe(new[] { anna.Id, beat.Id }.OrderBy(id => id));
        reused.NewMemberClientIds.ShouldBe(new[] { anna.Id, beat.Id }.OrderBy(id => id));

        var merged = plan.Groups[1];
        merged.Name.ShouldBe("Staplerfahrer");
        merged.Existed.ShouldBeFalse();
        merged.ExistingGroupId.ShouldBeNull();
        merged.QualificationIds.ShouldBe(new[] { forklift.Id, forkliftTwin.Id }.OrderBy(id => id));
        merged.MemberClientIds.ShouldBe(new[] { anna.Id, beat.Id }.OrderBy(id => id));

        plan.Skipped.ShouldBe(new[] { new SkippedQualificationGroup("Kranführer", 1) });
    }

    [Test]
    public void Plan_OrdersGroupsByOrdinalDisplayName()
    {
        var zeta = Qualification("Zeta");
        var alpha = Qualification("alpha");

        var plan = QualificationGroupPlanner.Plan(
            [Holder(alpha, zeta)],
            [alpha, zeta],
            [],
            new QualificationGroupPlanContext(GermanFirst, Today, null, null, 1, true));

        plan.Groups.Select(g => g.Name).ShouldBe(new[] { "Zeta", "alpha" });
        plan.Warnings.ShouldBeEmpty();
    }

    [Test]
    public void Plan_CountsAClientAsConsidered_BeforeSkippingItAsAlreadyGrouped_AndOutOfScopeClientsNot()
    {
        var forklift = Qualification("Staplerfahrer");
        var scopeRoot = Guid.NewGuid();
        var groupedElsewhere = Holder(forklift);
        groupedElsewhere.GroupItems.Add(Membership(groupedElsewhere.Id, scopeRoot));
        groupedElsewhere.GroupItems.Add(Membership(groupedElsewhere.Id, Guid.NewGuid()));
        var outOfScope = Holder(forklift);

        var plan = QualificationGroupPlanner.Plan(
            [groupedElsewhere, outOfScope],
            [forklift],
            [],
            new QualificationGroupPlanContext(
                GermanFirst, Today, scopeRoot, new HashSet<Guid> { scopeRoot }, 1, false, new HashSet<Guid> { scopeRoot }));

        plan.TotalClients.ShouldBe(2);
        plan.ConsideredClients.ShouldBe(1);
        plan.SkippedAlreadyGroupedCount.ShouldBe(1);
        plan.ClientsWithoutQualificationCount.ShouldBe(0);
        plan.Groups.ShouldBeEmpty();
        plan.Skipped.ShouldBeEmpty();
    }

    private static Qualification Qualification(string german)
    {
        var name = new MultiLanguage();
        name.SetValue("de", german);
        return new Qualification { Id = Guid.NewGuid(), Name = name };
    }

    private static Client Holder(params Qualification[] qualifications)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "Test", Type = EntityTypeEnum.Employee };
        foreach (var qualification in qualifications)
        {
            client.Qualifications.Add(new ClientQualification
            {
                Id = Guid.NewGuid(), ClientId = client.Id, QualificationId = qualification.Id
            });
        }

        return client;
    }

    private static GroupItem Membership(Guid clientId, Guid groupId) =>
        new() { Id = Guid.NewGuid(), ClientId = clientId, GroupId = groupId };
}
