// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for <see cref="ReplacementCandidatePoolResolver"/>: the pool is read under the root of the group and
/// split by the real memberships of the group's subtree; only people who can take over a replacement are in it, the
/// borrowable part comes ordered by id, and an unknown group has no pool.
/// </summary>
[TestFixture]
public sealed class ReplacementCandidatePoolResolverTests
{
    private static readonly Guid RootId = Guid.NewGuid();
    private static readonly Guid SubGroupId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groupClientService = null!;
    private ReplacementCandidatePoolResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.Get(SubGroupId).Returns(new Group { Id = SubGroupId, Root = RootId });
        _groupClientService = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _resolver = new ReplacementCandidatePoolResolver(_clientRepository, _groupRepository, _groupClientService);
    }

    private void GivenUnderRoot(params Client[] clients)
        => _clientRepository.GetActiveClientsWithAddressesForGroupsAsync(
                Arg.Is<List<Guid>>(ids => ids.Count == 1 && ids[0] == RootId), Arg.Any<CancellationToken>())
            .Returns(clients.ToList());

    private void GivenRealMembers(params Client[] members)
        => _groupClientService.GetRealClientIdsFromGroupAndSubgroups(SubGroupId, Arg.Any<CancellationToken>())
            .Returns(members.Select(member => member.Id).ToList());

    private static Client Person(EntityTypeEnum type)
        => new() { Id = Guid.NewGuid(), Name = type.ToString(), FirstName = string.Empty, Type = type };

    [Test]
    public async Task TheMembersOfTheSubtree_AreInGroup_AndTheRestUnderTheRoot_IsBorrowable()
    {
        // Arrange
        var member = Person(EntityTypeEnum.Employee);
        var sibling = Person(EntityTypeEnum.Employee);
        GivenUnderRoot(member, sibling);
        GivenRealMembers(member);

        // Act
        var pool = await _resolver.ResolveAsync(SubGroupId, CancellationToken.None);

        // Assert
        pool.ShouldNotBeNull();
        pool.RootGroupId.ShouldBe(RootId);
        pool.InGroup.Select(c => c.Id).ShouldBe([member.Id]);
        pool.CrossGroup.Select(c => c.Id).ShouldBe([sibling.Id]);
    }

    [Test]
    public async Task OnlyRealMemberships_DecideWhoIsInGroup()
    {
        // Arrange: the reader that also counts the temporary memberships of scenarios would make the sibling a member.
        var member = Person(EntityTypeEnum.Employee);
        var sibling = Person(EntityTypeEnum.Employee);
        GivenUnderRoot(member, sibling);
        GivenRealMembers(member);
        _groupClientService.GetAllClientIdsFromGroupAndSubgroups(SubGroupId).Returns(new List<Guid> { member.Id, sibling.Id });

        // Act
        var pool = await _resolver.ResolveAsync(SubGroupId, CancellationToken.None);

        // Assert
        pool.ShouldNotBeNull();
        pool.CrossGroup.Select(c => c.Id).ShouldBe([sibling.Id]);
    }

    [Test]
    public async Task Customers_AreNotCandidates_InGroupOrAcrossGroups()
    {
        // Arrange
        var member = Person(EntityTypeEnum.Employee);
        var memberCustomer = Person(EntityTypeEnum.Customer);
        var external = Person(EntityTypeEnum.ExternEmp);
        var customer = Person(EntityTypeEnum.Customer);
        GivenUnderRoot(member, memberCustomer, external, customer);
        GivenRealMembers(member, memberCustomer);

        // Act
        var pool = await _resolver.ResolveAsync(SubGroupId, CancellationToken.None);

        // Assert
        pool.ShouldNotBeNull();
        pool.InGroup.Select(c => c.Id).ShouldBe([member.Id]);
        pool.CrossGroup.Select(c => c.Id).ShouldBe([external.Id]);
    }

    [Test]
    public async Task TheBorrowablePeople_ComeOrderedById()
    {
        // Arrange
        var people = Enumerable.Range(0, 6).Select(_ => Person(EntityTypeEnum.Employee)).ToArray();
        GivenUnderRoot(people);
        GivenRealMembers();

        // Act
        var pool = await _resolver.ResolveAsync(SubGroupId, CancellationToken.None);

        // Assert
        pool.ShouldNotBeNull();
        pool.CrossGroup.Select(c => c.Id).ShouldBe(people.Select(c => c.Id).OrderBy(id => id));
    }

    [Test]
    public async Task ARootGroup_IsItsOwnRoot()
    {
        // Arrange
        var member = Person(EntityTypeEnum.Employee);
        _groupRepository.Get(RootId).Returns(new Group { Id = RootId });
        GivenUnderRoot(member);
        _groupClientService.GetRealClientIdsFromGroupAndSubgroups(RootId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { member.Id });

        // Act
        var pool = await _resolver.ResolveAsync(RootId, CancellationToken.None);

        // Assert
        pool.ShouldNotBeNull();
        pool.RootGroupId.ShouldBe(RootId);
        pool.InGroup.Select(c => c.Id).ShouldBe([member.Id]);
    }

    [Test]
    public async Task AnUnknownGroup_HasNoPool_AndNoClientIsRead()
    {
        // Arrange
        var unknownGroupId = Guid.NewGuid();
        _groupRepository.Get(unknownGroupId).Returns((Group?)null);

        // Act
        var pool = await _resolver.ResolveAsync(unknownGroupId, CancellationToken.None);

        // Assert
        pool.ShouldBeNull();
        await _clientRepository.DidNotReceive().GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>());
    }
}
