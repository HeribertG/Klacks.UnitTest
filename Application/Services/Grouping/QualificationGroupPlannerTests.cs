// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class QualificationGroupPlannerTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly IReadOnlyList<string> GermanFirst = ["de", "en", "fr", "it"];
    private static readonly IReadOnlyList<string> FrenchFirst = ["fr", "de", "en", "it"];

    private Qualification _forklift = null!;
    private Qualification _firstAid = null!;
    private Qualification _crane = null!;

    [SetUp]
    public void SetUp()
    {
        _forklift = Qualification("Staplerfahrer", "Cariste");
        _firstAid = Qualification("Erste Hilfe", "Premiers secours");
        _crane = Qualification("Kranführer", "Grutier");
    }

    [Test]
    public void Plan_ClientWithSeveralQualifications_JoinsEveryGroup()
    {
        var anna = Holder(_forklift, _firstAid, _crane);
        var beat = Holder(_forklift, _firstAid, _crane);

        var plan = Plan([anna, beat]);

        plan.Groups.Select(g => g.Name).ShouldBe(["Erste Hilfe", "Kranführer", "Staplerfahrer"]);
        plan.Groups.ShouldAllBe(g => g.MemberClientIds.Contains(anna.Id) && g.MemberClientIds.Contains(beat.Id));
        plan.Groups.ShouldAllBe(g => !g.Existed && g.NewMemberClientIds.Count == 2);
    }

    [Test]
    public void Plan_QualificationBelowMinimum_IsSkippedWithItsCount()
    {
        var plan = Plan([Holder(_forklift, _crane), Holder(_forklift)], minMembers: 2);

        plan.Groups.ShouldHaveSingleItem().Name.ShouldBe("Staplerfahrer");
        var skipped = plan.Skipped.ShouldHaveSingleItem();
        skipped.Name.ShouldBe("Kranführer");
        skipped.MemberCount.ShouldBe(1);
    }

    [Test]
    public void Plan_MinMembersOne_GroupsEverySingleHolder()
    {
        var plan = Plan([Holder(_crane)], minMembers: 1);

        plan.Groups.ShouldHaveSingleItem().Name.ShouldBe("Kranführer");
    }

    [Test]
    public void Plan_NamesGroupsInTheFirstLanguageThatHasAName()
    {
        var plan = Plan([Holder(_forklift), Holder(_forklift)], languages: FrenchFirst);

        plan.Groups.ShouldHaveSingleItem().Name.ShouldBe("Cariste");
    }

    [Test]
    public void Plan_FallsBackToNextLanguage_WhenFirstHasNoName()
    {
        var onlyGerman = Qualification("Schweisser", null);

        var plan = Plan([Holder(onlyGerman), Holder(onlyGerman)], [onlyGerman], languages: FrenchFirst);

        plan.Groups.ShouldHaveSingleItem().Name.ShouldBe("Schweisser");
    }

    [Test]
    public void Plan_ExpiredFutureAndDeletedQualifications_DoNotCount()
    {
        var expired = Holder();
        expired.Qualifications.Add(Holding(expired.Id, _forklift, until: Today.AddDays(-1)));
        var future = Holder();
        future.Qualifications.Add(Holding(future.Id, _forklift, from: Today.AddDays(1)));
        var deleted = Holder();
        var deletedHolding = Holding(deleted.Id, _forklift);
        deletedHolding.IsDeleted = true;
        deleted.Qualifications.Add(deletedHolding);
        var valid = Holder();
        valid.Qualifications.Add(Holding(valid.Id, _forklift, from: Today, until: Today));

        var plan = Plan([expired, future, deleted, valid], minMembers: 1);

        plan.Groups.ShouldHaveSingleItem().MemberClientIds.ShouldBe([valid.Id]);
        plan.ClientsWithoutQualificationCount.ShouldBe(3);
    }

    [Test]
    public void Plan_ExistingGroupUnderParent_IsReused_AndOnlyNonMembersAreNew()
    {
        var parentId = Guid.NewGuid();
        var existing = new Group { Id = Guid.NewGuid(), Name = "staplerfahrer", Parent = parentId };
        var member = Holder(_forklift);
        member.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = member.Id, GroupId = existing.Id });
        var newcomer = Holder(_forklift);

        var plan = Plan([member, newcomer], groups: [existing], parentId: parentId);

        var group = plan.Groups.ShouldHaveSingleItem();
        group.Existed.ShouldBeTrue();
        group.ExistingGroupId.ShouldBe(existing.Id);
        group.Name.ShouldBe("staplerfahrer");
        group.MemberClientIds.Count.ShouldBe(2);
        group.NewMemberClientIds.ShouldBe([newcomer.Id]);
    }

    [Test]
    public void Plan_EndedMembershipOfExistingGroup_CountsAsNewMember()
    {
        var parentId = Guid.NewGuid();
        var existing = new Group { Id = Guid.NewGuid(), Name = "Staplerfahrer", Parent = parentId };
        var former = Holder(_forklift);
        former.GroupItems.Add(new GroupItem
        {
            Id = Guid.NewGuid(), ClientId = former.Id, GroupId = existing.Id,
            ValidUntil = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)
        });

        var plan = Plan([former, Holder(_forklift)], groups: [existing], parentId: parentId);

        plan.Groups.Single().NewMemberClientIds.ShouldContain(former.Id);
    }

    [Test]
    public void Plan_GroupWithSameNameUnderAnotherParent_IsNotReused()
    {
        var elsewhere = new Group { Id = Guid.NewGuid(), Name = "Staplerfahrer", Parent = Guid.NewGuid() };

        var plan = Plan([Holder(_forklift), Holder(_forklift)], groups: [elsewhere], parentId: Guid.NewGuid());

        plan.Groups.Single().Existed.ShouldBeFalse();
    }

    [Test]
    public void Plan_PendingParent_PlansEveryGroupAsNew()
    {
        var orphan = new Group { Id = Guid.NewGuid(), Name = "Staplerfahrer", Parent = null };

        var plan = Plan([Holder(_forklift), Holder(_forklift)], groups: [orphan], parentId: null);

        plan.Groups.Single().Existed.ShouldBeFalse();
    }

    [Test]
    public void Plan_Scope_ConsidersOnlyMembersOfTheSubtree()
    {
        var scopeRoot = Guid.NewGuid();
        var scopeChild = Guid.NewGuid();
        var inRoot = Holder(_forklift);
        inRoot.GroupItems.Add(Membership(inRoot.Id, scopeRoot));
        var inChild = Holder(_forklift);
        inChild.GroupItems.Add(Membership(inChild.Id, scopeChild));
        var outside = Holder(_forklift);
        outside.GroupItems.Add(Membership(outside.Id, Guid.NewGuid()));
        var noGroup = Holder(_forklift);

        var plan = Plan([inRoot, inChild, outside, noGroup], scope: new HashSet<Guid> { scopeRoot, scopeChild }, parentId: scopeRoot);

        plan.ConsideredClients.ShouldBe(2);
        plan.Groups.Single().MemberClientIds.OrderBy(id => id).ShouldBe(new[] { inRoot.Id, inChild.Id }.OrderBy(id => id));
    }

    [Test]
    public void Plan_Scope_EndedOrScenarioMembershipDoesNotCount()
    {
        var scopeRoot = Guid.NewGuid();
        var ended = Holder(_forklift);
        ended.GroupItems.Add(new GroupItem
        {
            Id = Guid.NewGuid(), ClientId = ended.Id, GroupId = scopeRoot,
            ValidUntil = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        var scenario = Holder(_forklift);
        scenario.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = scenario.Id, GroupId = scopeRoot, AnalyseToken = Guid.NewGuid() });

        var plan = Plan([ended, scenario], scope: new HashSet<Guid> { scopeRoot }, parentId: scopeRoot);

        plan.ConsideredClients.ShouldBe(0);
        plan.Groups.ShouldBeEmpty();
    }

    [Test]
    public void Plan_IncludeAlreadyGroupedFalse_SkipsClientsWithOtherMemberships()
    {
        var grouped = Holder(_forklift);
        grouped.GroupItems.Add(Membership(grouped.Id, Guid.NewGuid()));
        var free1 = Holder(_forklift);
        var free2 = Holder(_forklift);

        var plan = Plan([grouped, free1, free2], includeAlreadyGrouped: false);

        plan.SkippedAlreadyGroupedCount.ShouldBe(1);
        plan.Groups.Single().MemberClientIds.ShouldNotContain(grouped.Id);
    }

    [Test]
    public void Plan_IncludeAlreadyGroupedFalse_WithScope_IgnoresMembershipsInsideTheSubtree()
    {
        var scopeRoot = Guid.NewGuid();
        var insideOnly = Holder(_forklift);
        insideOnly.GroupItems.Add(Membership(insideOnly.Id, scopeRoot));
        var alsoOutside = Holder(_forklift);
        alsoOutside.GroupItems.Add(Membership(alsoOutside.Id, scopeRoot));
        alsoOutside.GroupItems.Add(Membership(alsoOutside.Id, Guid.NewGuid()));

        var plan = Plan([insideOnly, alsoOutside], scope: new HashSet<Guid> { scopeRoot }, parentId: scopeRoot,
            includeAlreadyGrouped: false, minMembers: 1, ownSubtree: new HashSet<Guid> { scopeRoot });

        plan.SkippedAlreadyGroupedCount.ShouldBe(1);
        plan.Groups.Single().MemberClientIds.ShouldBe([insideOnly.Id]);
    }

    [Test]
    public void Plan_IncludeAlreadyGroupedFalse_WithoutScope_IgnoresMembershipsInTheQualificationRootSubtree()
    {
        var rootId = Guid.NewGuid();
        var forkliftGroupId = Guid.NewGuid();
        var existing = new Group { Id = forkliftGroupId, Name = "Staplerfahrer", Parent = rootId };
        var rerunMember = Holder(_forklift, _firstAid);
        rerunMember.GroupItems.Add(Membership(rerunMember.Id, forkliftGroupId));
        var groupedElsewhere = Holder(_forklift, _firstAid);
        groupedElsewhere.GroupItems.Add(Membership(groupedElsewhere.Id, Guid.NewGuid()));

        var plan = Plan([rerunMember, groupedElsewhere], groups: [existing], parentId: rootId, includeAlreadyGrouped: false,
            minMembers: 1, ownSubtree: new HashSet<Guid> { rootId, forkliftGroupId });

        plan.SkippedAlreadyGroupedCount.ShouldBe(1);
        plan.Groups.Single(g => g.Name == "Erste Hilfe").NewMemberClientIds.ShouldBe([rerunMember.Id]);
        plan.Groups.Single(g => g.Name == "Staplerfahrer").NewMemberClientIds.ShouldBeEmpty();
    }

    [Test]
    public void Plan_QualificationWithoutNameInAnyLanguage_IsIgnoredWithItsOwnWarning()
    {
        var unnamed = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage() };

        var plan = Plan([Holder(unnamed), Holder(unnamed)], [unnamed]);

        plan.Groups.ShouldBeEmpty();
        plan.Warnings.ShouldHaveSingleItem().ShouldContain("without a name");
    }

    [Test]
    public void Plan_TwoQualificationsWithTheSameName_ShareOneGroupAndWarn()
    {
        var twin = Qualification("Staplerfahrer", "Cariste B");

        var plan = Plan([Holder(_forklift), Holder(twin)], [_forklift, twin]);

        var group = plan.Groups.ShouldHaveSingleItem();
        group.QualificationIds.Count.ShouldBe(2);
        group.MemberClientIds.Count.ShouldBe(2);
        plan.Warnings.ShouldContain(w => w.Contains("Staplerfahrer"));
    }

    [Test]
    public void Plan_QualificationWithoutMasterRecord_IsIgnoredWithWarning()
    {
        var unknown = Qualification("Ghost", "Ghost");

        var plan = Plan([Holder(unknown), Holder(unknown)], [_forklift]);

        plan.Groups.ShouldBeEmpty();
        plan.Warnings.ShouldHaveSingleItem().ShouldContain("no longer exists");
    }

    private QualificationGroupPlan Plan(
        IReadOnlyList<Client> clients,
        IReadOnlyList<Qualification>? qualifications = null,
        IReadOnlyList<Group>? groups = null,
        Guid? parentId = null,
        IReadOnlySet<Guid>? scope = null,
        int minMembers = 2,
        bool includeAlreadyGrouped = true,
        IReadOnlyList<string>? languages = null,
        IReadOnlySet<Guid>? ownSubtree = null) =>
        QualificationGroupPlanner.Plan(
            clients,
            qualifications ?? [_forklift, _firstAid, _crane],
            groups ?? [],
            new QualificationGroupPlanContext(languages ?? GermanFirst, Today, parentId, scope, minMembers, includeAlreadyGrouped, ownSubtree));

    private static Qualification Qualification(string german, string? french)
    {
        var name = new MultiLanguage();
        name.SetValue("de", german);
        name.SetValue("fr", french);
        return new Qualification { Id = Guid.NewGuid(), Name = name };
    }

    private static Client Holder(params Qualification[] qualifications)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "Test", Type = EntityTypeEnum.Employee };
        foreach (var qualification in qualifications)
        {
            client.Qualifications.Add(Holding(client.Id, qualification));
        }

        return client;
    }

    private static ClientQualification Holding(Guid clientId, Qualification qualification, DateOnly? from = null, DateOnly? until = null) =>
        new() { Id = Guid.NewGuid(), ClientId = clientId, QualificationId = qualification.Id, ValidFrom = from, ValidUntil = until };

    private static GroupItem Membership(Guid clientId, Guid groupId) =>
        new() { Id = Guid.NewGuid(), ClientId = clientId, GroupId = groupId };
}
