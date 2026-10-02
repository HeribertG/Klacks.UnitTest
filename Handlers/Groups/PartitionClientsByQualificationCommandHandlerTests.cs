// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class PartitionClientsByQualificationCommandHandlerTests
{
    private const string UserName = "tester";
    private static readonly DateTime CompanyToday = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    private IClientRepository _clientRepository = null!;
    private IQualificationRepository _qualificationRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private IInstallationLanguageResolver _languageResolver = null!;
    private IGroupVisibilityRepository _groupVisibilityRepository = null!;
    private IGroupVisibilityPreservationService _visibilityPreservation = null!;
    private List<Group> _groups = null!;
    private List<Client> _employees = null!;
    private Qualification _forklift = null!;
    private Qualification _firstAid = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _qualificationRepository = Substitute.For<IQualificationRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _languageResolver = Substitute.For<IInstallationLanguageResolver>();
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns("de");
        _visibilityPreservation = Substitute.For<IGroupVisibilityPreservationService>();
        _groupVisibilityRepository = Substitute.For<IGroupVisibilityRepository>();

        _forklift = Qualification("Staplerfahrer", "Forklift driver", "Cariste");
        _firstAid = Qualification("Erste Hilfe", "First aid", "Premiers secours");
        _qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Qualification> { _forklift, _firstAid });

        _groups = [];
        _groupRepository.List().Returns(_ => _groups);
        _employees = [];
        _clientRepository.GetByTypeWithQualificationsAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());
        _clientRepository.GetByTypeWithQualificationsAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(_ => _employees);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<QualificationApplyOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<QualificationApplyOutcome>>>()());
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
    }

    [Test]
    public async Task Preview_PlansRootAndGroups_WritesNothing()
    {
        _employees = [Holder(_forklift, _firstAid), Holder(_forklift, _firstAid)];

        var result = await Handler().Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.ParentGroupName.ShouldBe("Qualifikationen");
        result.ParentExisted.ShouldBeFalse();
        result.Groups.Select(g => g.Name).ShouldBe(["Erste Hilfe", "Staplerfahrer"]);
        result.AssignedCount.ShouldBe(4);
        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(Arg.Any<Func<Task<QualificationApplyOutcome>>>());
    }

    [Test]
    public async Task Apply_CreatesRootFirst_ThenQualificationGroups_AndOneMembershipPerHeldQualification()
    {
        var anna = Holder(_forklift, _firstAid);
        var beat = Holder(_forklift, _firstAid);
        _employees = [anna, beat];
        var added = new List<Group>();
        await _groupRepository.Add(Arg.Do<Group>(added.Add));
        var items = new List<GroupItem>();
        await _groupItemRepository.Add(Arg.Do<GroupItem>(items.Add));

        var result = await Handler().Handle(Command(apply: true), CancellationToken.None);

        added.Count.ShouldBe(3);
        added[0].Name.ShouldBe("Qualifikationen");
        added[0].Parent.ShouldBeNull();
        added.Skip(1).ShouldAllBe(g => g.Parent == added[0].Id);
        items.Count.ShouldBe(4);
        items.Count(i => i.ClientId == anna.Id).ShouldBe(2);
        items.ShouldAllBe(i => i.ValidFrom == CompanyToday);
        result.Applied.ShouldBeTrue();
        result.VerifiedCount.ShouldBe(4);
        result.ParentGroupId.ShouldBe(added[0].Id);
        result.Groups.ShouldAllBe(g => g.GroupId != null);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Apply_ExistingMembershipsAreNeverEnded()
    {
        var other = new Group { Id = Guid.NewGuid(), Name = "Bern" };
        _groups = [other];
        var anna = Holder(_forklift);
        anna.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = anna.Id, GroupId = other.Id });
        _employees = [anna, Holder(_forklift)];

        await Handler().Handle(Command(apply: true), CancellationToken.None);

        await _groupItemRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _groupItemRepository.DidNotReceive().Put(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task Apply_ReRun_ReusesRootAndGroups_AddsOnlyMissingMemberships()
    {
        var root = new Group { Id = Guid.NewGuid(), Name = "Qualifikationen" };
        var forkliftGroup = new Group { Id = Guid.NewGuid(), Name = "Staplerfahrer", Parent = root.Id };
        _groups = [root, forkliftGroup];
        var member = Holder(_forklift);
        member.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = member.Id, GroupId = forkliftGroup.Id });
        var newcomer = Holder(_forklift);
        _employees = [member, newcomer];
        var items = new List<GroupItem>();
        await _groupItemRepository.Add(Arg.Do<GroupItem>(items.Add));

        var result = await Handler().Handle(Command(apply: true), CancellationToken.None);

        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
        items.ShouldHaveSingleItem().ClientId.ShouldBe(newcomer.Id);
        items[0].GroupId.ShouldBe(forkliftGroup.Id);
        result.AlreadyMemberCount.ShouldBe(1);
        result.ParentExisted.ShouldBeTrue();
        result.Groups.Single().Existed.ShouldBeTrue();
    }

    [Test]
    public async Task Preview_ReRun_ReportsExistingGroupWithNewMemberCount()
    {
        var root = new Group { Id = Guid.NewGuid(), Name = "Qualifikationen" };
        var forkliftGroup = new Group { Id = Guid.NewGuid(), Name = "Staplerfahrer", Parent = root.Id };
        _groups = [root, forkliftGroup];
        var member = Holder(_forklift);
        member.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = member.Id, GroupId = forkliftGroup.Id });
        _employees = [member, Holder(_forklift), Holder(_forklift)];

        var result = await Handler().Handle(Command(apply: false), CancellationToken.None);

        var group = result.Groups.Single();
        group.Existed.ShouldBeTrue();
        group.MemberCount.ShouldBe(3);
        group.NewMemberCount.ShouldBe(2);
    }

    [Test]
    public async Task Handle_ScopeGroup_PutsGroupsUnderIt_AndConsidersOnlyItsSubtree()
    {
        var winterthur = new Group { Id = Guid.NewGuid(), Name = "Winterthur" };
        var seuzach = new Group { Id = Guid.NewGuid(), Name = "Seuzach", Parent = winterthur.Id };
        var zurich = new Group { Id = Guid.NewGuid(), Name = "Zürich" };
        _groups = [winterthur, seuzach, zurich];
        var inCity = Member(Holder(_forklift), winterthur.Id);
        var inSubgroup = Member(Holder(_forklift), seuzach.Id);
        var elsewhere = Member(Holder(_forklift), zurich.Id);
        _employees = [inCity, inSubgroup, elsewhere];
        var added = new List<Group>();
        await _groupRepository.Add(Arg.Do<Group>(added.Add));
        var items = new List<GroupItem>();
        await _groupItemRepository.Add(Arg.Do<GroupItem>(items.Add));

        var result = await Handler().Handle(
            Command(apply: true, scopeGroupId: winterthur.Id, scopeGroupName: winterthur.Name), CancellationToken.None);

        added.ShouldHaveSingleItem().Parent.ShouldBe(winterthur.Id);
        added[0].Name.ShouldBe("Staplerfahrer");
        items.Select(i => i.ClientId).ShouldBe([inCity.Id, inSubgroup.Id], ignoreOrder: true);
        result.IsScoped.ShouldBeTrue();
        result.ConsideredClients.ShouldBe(2);
        result.ParentGroupName.ShouldBe("Winterthur");
    }

    [Test]
    public async Task Handle_NamesFollowInstallationLanguage_WithBaseLanguageFallback()
    {
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns("fr-CH");
        _employees = [Holder(_forklift), Holder(_forklift)];

        var result = await Handler().Handle(Command(apply: false), CancellationToken.None);

        result.Groups.Single().Name.ShouldBe("Cariste");
        result.ParentGroupName.ShouldBe("Qualifications");
    }

    [Test]
    public async Task Handle_TwoTopLevelRootsWithTheRootName_IsRefused()
    {
        _groups = [new Group { Id = Guid.NewGuid(), Name = "Qualifikationen" }, new Group { Id = Guid.NewGuid(), Name = "Qualifikationen" }];
        _employees = [Holder(_forklift), Holder(_forklift)];

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => Handler().Handle(Command(apply: false), CancellationToken.None));

        exception.Message.ShouldContain("Qualifikationen");
        exception.Message.ShouldContain("rename or delete the duplicates");
        exception.Message.ShouldNotContain("rootGroupName");
    }

    [Test]
    public async Task Preview_ReusedRootWithRestrictedViewers_ReportsHowManyPeopleTheyWouldAdditionallySee()
    {
        var root = new Group { Id = Guid.NewGuid(), Name = "Qualifikationen" };
        var forkliftGroup = new Group { Id = Guid.NewGuid(), Name = "Staplerfahrer", Parent = root.Id };
        _groups = [root, forkliftGroup];
        var alreadyInside = Member(Holder(_forklift, _firstAid), forkliftGroup.Id);
        _employees = [alreadyInside, Holder(_forklift), Holder(_firstAid), Holder(_firstAid)];
        _groupVisibilityRepository.CountNonAdminUsersSeeingGroupAsync(root.Id, Arg.Any<CancellationToken>()).Returns(2);

        var result = await Handler().Handle(Command(apply: false, minMembers: 1), CancellationToken.None);

        result.RestrictedUsersSeeingRootCount.ShouldBe(2);
        result.ClientsNewlyVisibleToThemCount.ShouldBe(3);
    }

    [Test]
    public async Task Preview_NewRootOrScopeGroup_ReportsNoVisibilityWidening()
    {
        var winterthur = new Group { Id = Guid.NewGuid(), Name = "Winterthur" };
        _groups = [winterthur];
        _employees = [Member(Holder(_forklift), winterthur.Id), Member(Holder(_forklift), winterthur.Id)];
        _groupVisibilityRepository.CountNonAdminUsersSeeingGroupAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(5);

        var unscoped = await Handler().Handle(Command(apply: false), CancellationToken.None);
        var scoped = await Handler().Handle(
            Command(apply: false, scopeGroupId: winterthur.Id, scopeGroupName: winterthur.Name), CancellationToken.None);

        unscoped.RestrictedUsersSeeingRootCount.ShouldBe(0);
        scoped.RestrictedUsersSeeingRootCount.ShouldBe(0);
        await _groupVisibilityRepository.DidNotReceiveWithAnyArgs().CountNonAdminUsersSeeingGroupAsync(default, default);
    }

    [Test]
    public async Task Handle_MinMembersBelowOne_IsClampedToOne()
    {
        _employees = [Holder(_forklift)];

        var result = await Handler().Handle(Command(apply: false, minMembers: 0), CancellationToken.None);

        result.MinMembers.ShouldBe(1);
        result.Groups.ShouldHaveSingleItem();
    }

    [Test]
    public async Task Apply_VerificationMismatch_ThrowsSoTheTransactionRollsBack()
    {
        _employees = [Holder(_forklift), Holder(_forklift)];
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        await Should.ThrowAsync<SkillVerificationException>(
            () => Handler().Handle(Command(apply: true), CancellationToken.None));
    }

    [Test]
    public async Task Handle_RestrictedCaller_IsForbiddenBeforeAnythingIsRead()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        var handler = new PartitionClientsByQualificationCommandHandler(
            _clientRepository, _qualificationRepository, _groupRepository, _groupItemRepository, _unitOfWork,
            _companyClock, _languageResolver, _groupVisibilityRepository, _visibilityPreservation, guard);

        await Should.ThrowAsync<ForbiddenException>(() => handler.Handle(Command(apply: true), CancellationToken.None));

        await _clientRepository.DidNotReceiveWithAnyArgs()
            .GetByTypeWithQualificationsAndGroupItemsAsync(default, default);
        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
    }

    private PartitionClientsByQualificationCommandHandler Handler() => new(
        _clientRepository, _qualificationRepository, _groupRepository, _groupItemRepository, _unitOfWork,
        _companyClock, _languageResolver, _groupVisibilityRepository, _visibilityPreservation, TestGroupWriteVisibility.UnrestrictedGroups());

    private static PartitionClientsByQualificationCommand Command(
        bool apply, Guid? scopeGroupId = null, string? scopeGroupName = null, int minMembers = 2) =>
        new([EntityTypeEnum.Employee], scopeGroupId, scopeGroupName, minMembers, IncludeAlreadyGrouped: true,
            ValidFrom: null, apply, UserName);

    private static Qualification Qualification(string german, string english, string french)
    {
        var name = new MultiLanguage();
        name.SetValue("de", german);
        name.SetValue("en", english);
        name.SetValue("fr", french);
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

    private static Client Member(Client client, Guid groupId)
    {
        client.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = client.Id, GroupId = groupId });
        return client;
    }
}
