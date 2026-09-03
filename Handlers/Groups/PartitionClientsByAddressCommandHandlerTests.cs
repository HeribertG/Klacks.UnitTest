// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PartitionClientsByAddressCommandHandler: a preview (Apply=false) never touches a
/// repository or the unit of work, an apply creates the missing groups top-down and the memberships in
/// one commit, an already-existing group (by name under the right parent) is reused instead of
/// duplicated, a client that already holds the target membership is left untouched (the shape a second
/// apply run takes), and a verification mismatch rolls the whole write back by throwing.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class PartitionClientsByAddressCommandHandlerTests
{
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private PartitionClientsByAddressCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        _handler = new PartitionClientsByAddressCommandHandler(
            _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock);

        _groupRepository.List().Returns(new List<Group>());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((GroupItem?)null);
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
    }

    private static PartitionClientsByAddressCommand Command(
        bool apply,
        GroupPartitionLevelEnum level = GroupPartitionLevelEnum.Canton,
        bool includeAlreadyGrouped = false,
        DateTime? validFrom = null) =>
        new(level, EntityTypeEnum.Employee, RootGroupId: null, RootGroupName: null,
            includeAlreadyGrouped, validFrom, apply, "tester");

    private static Client Bern()
    {
        var clientId = Guid.NewGuid();
        return new Client
        {
            Id = clientId,
            FirstName = "Anna",
            Name = "Meier",
            Type = EntityTypeEnum.Employee,
            Addresses = new List<Address>
            {
                new() { ClientId = clientId, Type = AddressTypeEnum.Employee, State = "BE", City = "Bern", ValidFrom = CompanyToday }
            }
        };
    }

    [Test]
    public async Task Preview_DoesNotPersistAnything()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.AssignedCount.ShouldBe(0);
        result.Groups.Count.ShouldBe(2); // region root + canton
        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Apply_CreatesMissingGroupsTopDown_AndMemberships_CommitsOnce()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.Applied.ShouldBeTrue();
        result.AssignedCount.ShouldBe(1);
        result.VerifiedCount.ShouldBe(1);
        result.Groups.ShouldAllBe(g => g.GroupId != null);
        await _groupRepository.Received(2).Add(Arg.Any<Group>());
        await _groupItemRepository.Received(1).Add(Arg.Any<GroupItem>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Apply_ReusesExistingGroup_ByNameUnderTheRightParent_InsteadOfCreatingIt()
    {
        var regionId = Guid.NewGuid();
        var beId = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = regionId, Name = "Deutschschweiz Mitte", Parent = null },
            new() { Id = beId, Name = "BE", Parent = regionId }
        });
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.GroupId == beId));
        result.Groups.Single(g => g.Name == "BE").GroupId.ShouldBe(beId);
    }

    [Test]
    public async Task Apply_ClientAlreadyMemberOfTargetGroup_IsANoOp_LikeASecondRun()
    {
        var regionId = Guid.NewGuid();
        var beId = Guid.NewGuid();
        var client = Bern();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = regionId, Name = "Deutschschweiz Mitte", Parent = null },
            new() { Id = beId, Name = "BE", Parent = regionId }
        });
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
        _groupItemRepository.GetByClientAndGroup(client.Id, beId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = client.Id, GroupId = beId });

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.AssignedCount.ShouldBe(0);
        result.AlreadyMemberCount.ShouldBe(1);
        result.VerifiedCount.ShouldBe(0);
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public void Apply_RollsBackByThrowing_WhenVerificationCountDoesNotMatch()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(0);

        Assert.ThrowsAsync<SkillVerificationException>(
            () => _handler.Handle(Command(apply: true), CancellationToken.None));
    }

    [Test]
    public async Task Apply_StampsTheRequestedValidFrom_OnNewGroupsAndMemberships()
    {
        var validFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        await _handler.Handle(Command(apply: true, validFrom: validFrom), CancellationToken.None);

        await _groupRepository.Received().Add(Arg.Is<Group>(g => g.ValidFrom == validFrom));
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ValidFrom == validFrom));
    }

    [Test]
    public async Task Apply_DefaultsToCompanyToday_WhenNoValidFromGiven()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        await _handler.Handle(Command(apply: true), CancellationToken.None);

        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ValidFrom == CompanyToday));
    }
}
