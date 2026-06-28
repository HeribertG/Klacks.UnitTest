// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for FillGroupByCriteriaCommandHandler: a preview (Apply=false) never persists, an apply
/// adds only clients that are not already members and commits once, and the contract filter is passed
/// through to the search.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class FillGroupByCriteriaCommandHandlerTests
{
    private IClientSearchRepository _searchRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private FillGroupByCriteriaCommandHandler _handler = null!;

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid NewClientId = Guid.NewGuid();
    private static readonly Guid MemberClientId = Guid.NewGuid();
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void Setup()
    {
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _handler = new FillGroupByCriteriaCommandHandler(
            _searchRepository, _groupItemRepository, _unitOfWork, _companyClock);

        _searchRepository.SearchAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
            Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem>
                {
                    new() { Id = NewClientId, FirstName = "Max", LastName = "Müller" },
                    new() { Id = MemberClientId, FirstName = "Eva", LastName = "Meier" }
                },
                TotalCount = 2
            });

        _groupItemRepository.GetByClientAndGroup(NewClientId, GroupId).Returns((GroupItem?)null);
        _groupItemRepository.GetByClientAndGroup(MemberClientId, GroupId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = MemberClientId, GroupId = GroupId });

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(1);
    }

    private static FillGroupByCriteriaCommand Command(bool apply, Guid? contractId = null, DateTime? validFrom = null) =>
        new(GroupId, "Bern", "BE", contractId, EntityTypeEnum.Employee, null, validFrom, apply, "tester");

    [Test]
    public async Task Preview_DoesNotPersist_AndReturnsAllMatches()
    {
        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        Assert.That(result.Applied, Is.False);
        Assert.That(result.TotalMatchCount, Is.EqualTo(2));
        Assert.That(result.Clients, Has.Count.EqualTo(2));
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
    public async Task Apply_StampsTheCompanyToday_WhenNoValidFromGiven()
    {
        await _handler.Handle(Command(apply: true), CancellationToken.None);

        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi =>
            gi.ClientId == NewClientId
            && gi.ValidFrom == CompanyToday && gi.ValidFrom!.Value.Kind == DateTimeKind.Utc));
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
    public async Task Apply_PassesContractFilter_ToSearch()
    {
        var contractId = Guid.NewGuid();

        await _handler.Handle(Command(apply: true, contractId: contractId), CancellationToken.None);

        await _searchRepository.Received(1).SearchAsync(
            Arg.Any<string?>(), "BE", EntityTypeEnum.Employee, contractId, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
