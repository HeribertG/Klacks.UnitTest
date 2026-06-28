// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddSelectedClientsToGroupCommandHandler: a preview (Apply=false) never persists and
/// reports eligible vs already-member counts, an apply adds only the non-members and commits once, a
/// verification mismatch rolls back by throwing, and stale selection ids (not resolving to a client)
/// are counted as not-found.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class AddSelectedClientsToGroupCommandHandlerTests
{
    private IClientRepository _clientRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private AddSelectedClientsToGroupCommandHandler _handler = null!;

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid NewClientId = Guid.NewGuid();
    private static readonly Guid MemberClientId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _handler = new AddSelectedClientsToGroupCommandHandler(
            _clientRepository, _groupItemRepository, _unitOfWork, _companyClock);

        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>
            {
                new() { Id = NewClientId, FirstName = "Max", Name = "Müller", Type = EntityTypeEnum.Employee },
                new() { Id = MemberClientId, FirstName = "Eva", Name = "Meier", Type = EntityTypeEnum.Employee }
            });

        _groupItemRepository.GetByClientAndGroup(NewClientId, GroupId).Returns((GroupItem?)null);
        _groupItemRepository.GetByClientAndGroup(MemberClientId, GroupId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = MemberClientId, GroupId = GroupId });

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(1);
    }

    private static AddSelectedClientsToGroupCommand Command(bool apply, DateTime? validFrom = null) =>
        new(GroupId, "Bern", new[] { NewClientId, MemberClientId }, validFrom, apply, "tester");

    [Test]
    public async Task Preview_DoesNotPersist_AndReportsEligibleAndAlreadyMember()
    {
        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        Assert.That(result.Applied, Is.False);
        Assert.That(result.RequestedCount, Is.EqualTo(2));
        Assert.That(result.FoundCount, Is.EqualTo(2));
        Assert.That(result.EligibleCount, Is.EqualTo(1));
        Assert.That(result.AlreadyMemberCount, Is.EqualTo(1));
        Assert.That(result.Clients, Has.Count.EqualTo(1));
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Apply_AddsOnlyNonMembers_AndCommitsOnce()
    {
        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        Assert.That(result.Applied, Is.True);
        Assert.That(result.AddedCount, Is.EqualTo(1));
        Assert.That(result.VerifiedCount, Is.EqualTo(1));
        Assert.That(result.AlreadyMemberCount, Is.EqualTo(1));
        await _groupItemRepository.Received(1).Add(
            Arg.Is<GroupItem>(gi => gi.ClientId == NewClientId && gi.GroupId == GroupId && gi.CurrentUserCreated == "tester"));
        await _groupItemRepository.DidNotReceive().Add(Arg.Is<GroupItem>(gi => gi.ClientId == MemberClientId));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Apply_StampsTheRequestedValidFrom_OnTheNewMembership()
    {
        var validFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        await _handler.Handle(Command(apply: true, validFrom: validFrom), CancellationToken.None);

        await _groupItemRepository.Received(1).Add(
            Arg.Is<GroupItem>(gi => gi.ClientId == NewClientId && gi.ValidFrom == validFrom));
    }

    [Test]
    public void Apply_RollsBackByThrowing_WhenVerificationCountDoesNotMatch()
    {
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(0);

        Assert.ThrowsAsync<SkillVerificationException>(
            () => _handler.Handle(Command(apply: true), CancellationToken.None));
    }

    [Test]
    public async Task CountsStaleSelectionIds_AsNotFound()
    {
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>
            {
                new() { Id = NewClientId, FirstName = "Max", Name = "Müller", Type = EntityTypeEnum.Employee }
            });

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        Assert.That(result.RequestedCount, Is.EqualTo(2));
        Assert.That(result.FoundCount, Is.EqualTo(1));
        Assert.That(result.NotFoundCount, Is.EqualTo(1));
        Assert.That(result.EligibleCount, Is.EqualTo(1));
    }
}
