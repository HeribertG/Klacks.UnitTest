// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
using Klacks.UnitTest.TestHelpers;

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
    private static readonly DateTime CompanyToday = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _handler = new AddSelectedClientsToGroupCommandHandler(
            _clientRepository, _groupItemRepository, _unitOfWork, TestGroupWriteVisibility.UnrestrictedGroups(), TestGroupWriteVisibility.AllClientsVisible(),
            TestGroupMembershipStart.Planner(_companyClock));

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

    private static readonly DateOnly NewClientEntry = new(2024, 2, 1);

    private AddSelectedClientsToGroupCommandHandler HandlerWithEntryDates() => new(
        _clientRepository, _groupItemRepository, _unitOfWork, TestGroupWriteVisibility.UnrestrictedGroups(),
        TestGroupWriteVisibility.AllClientsVisible(), TestGroupMembershipStart.Planner(_companyClock, (NewClientId, NewClientEntry)));

    [Test]
    public async Task Preview_DescribesTheStart_AndCountsThePersonWhoEnteredEarlier()
    {
        var result = await HandlerWithEntryDates().Handle(Command(apply: false), CancellationToken.None);

        var start = result.MembershipStart!;
        start.Mode.ShouldBe(GroupMembershipStartMode.Fixed);
        start.StartDateGiven.ShouldBeFalse();
        start.StartDate.ShouldBe("2026-10-20");
        start.PersonCount.ShouldBe(1);
        start.PersonsEnteredBeforeStartCount.ShouldBe(1);
        start.EarliestEntryDate.ShouldBe("2024-02-01");
    }

    [Test]
    public async Task Apply_InEntryDateMode_StartsThePersonOnTheirOwnEntryDate()
    {
        var result = await HandlerWithEntryDates().Handle(
            Command(apply: true) with { StartMode = GroupMembershipStartMode.EntryDate }, CancellationToken.None);

        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi =>
            gi.ClientId == NewClientId && gi.ValidFrom == new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        result.MembershipStart!.Mode.ShouldBe(GroupMembershipStartMode.EntryDate);
        result.MembershipStart.PersonsEnteredBeforeStartCount.ShouldBe(0);
    }

    [Test]
    public async Task Apply_InEntryDateMode_APersonWithoutAMembershipRecord_StartsOnTheCompanyToday()
    {
        await _handler.Handle(Command(apply: true) with { StartMode = GroupMembershipStartMode.EntryDate }, CancellationToken.None);

        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ClientId == NewClientId && gi.ValidFrom == CompanyToday));
    }

    [Test]
    public async Task Apply_WithAGivenDate_ReportsTheEarlierEntrant()
    {
        var validFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await HandlerWithEntryDates().Handle(Command(apply: true, validFrom: validFrom), CancellationToken.None);

        result.MembershipStart!.StartDateGiven.ShouldBeTrue();
        result.MembershipStart.StartDate.ShouldBe("2026-05-01");
        result.MembershipStart.PersonsEnteredBeforeStartCount.ShouldBe(1);
    }

    private static readonly Guid MemberItemId = Guid.NewGuid();
    private static readonly DateTime MemberEntry = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private AddSelectedClientsToGroupCommandHandler HandlerWithAnExistingMemberStartingLater()
    {
        _groupItemRepository.GetByClientAndGroup(MemberClientId, GroupId).Returns(new GroupItem
        {
            Id = MemberItemId,
            ClientId = MemberClientId,
            GroupId = GroupId,
            ValidFrom = CompanyToday
        });
        _groupItemRepository.GetValidFromByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DateTime?> { [MemberItemId] = MemberEntry });
        _groupItemRepository.PullForwardValidFromAsync(Arg.Any<IReadOnlyDictionary<Guid, DateTime>>(), Arg.Any<CancellationToken>())
            .Returns(1);
        return new AddSelectedClientsToGroupCommandHandler(
            _clientRepository, _groupItemRepository, _unitOfWork, TestGroupWriteVisibility.UnrestrictedGroups(),
            TestGroupWriteVisibility.AllClientsVisible(),
            TestGroupMembershipStart.Planner(_companyClock, _groupItemRepository, Substitute.For<IGroupRepository>(),
                (MemberClientId, new DateOnly(2020, 1, 1))));
    }

    [Test]
    public async Task EntryDate_Preview_AnnouncesThePullForward_AndWritesNothing()
    {
        var handler = HandlerWithAnExistingMemberStartingLater();

        var preview = await handler.Handle(
            Command(apply: false) with { StartMode = GroupMembershipStartMode.EntryDate }, CancellationToken.None);

        preview.MembershipStart!.ExistingMembersPulledForwardCount.ShouldBe(1);
        preview.MembershipStart.PulledForwardEarliestEntryDate.ShouldBe("2020-01-01");
        preview.MembershipStart.ExistingMembersStartingAfterEntryCount.ShouldBe(0);
        await _groupItemRepository.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task EntryDate_Apply_PullsTheExistingMembershipForwardToTheEntryDate_InOneBatch_AndVerifies()
    {
        var handler = HandlerWithAnExistingMemberStartingLater();

        var applied = await handler.Handle(
            Command(apply: true) with { StartMode = GroupMembershipStartMode.EntryDate }, CancellationToken.None);

        applied.MembershipStart!.ExistingMembersPulledForwardCount.ShouldBe(1);
        await _groupItemRepository.Received(1).PullForwardValidFromAsync(
            Arg.Is<IReadOnlyDictionary<Guid, DateTime>>(map => map.Count == 1 && map[MemberItemId] == MemberEntry),
            Arg.Any<CancellationToken>());
        await _groupItemRepository.Received(1).GetValidFromByIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EntryDate_Apply_APullForwardAloneStillRunsInsideATransaction()
    {
        var handler = HandlerWithAnExistingMemberStartingLater();
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = MemberClientId, FirstName = "Eva", Name = "Meier", Type = EntityTypeEnum.Employee } });

        var applied = await handler.Handle(
            Command(apply: true) with { StartMode = GroupMembershipStartMode.EntryDate }, CancellationToken.None);

        applied.AddedCount.ShouldBe(0);
        applied.MembershipStart!.ExistingMembersPulledForwardCount.ShouldBe(1);
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Fixed_Apply_NeverTouchesTheExistingMembership()
    {
        var handler = HandlerWithAnExistingMemberStartingLater();

        var applied = await handler.Handle(Command(apply: true), CancellationToken.None);

        applied.MembershipStart!.ExistingMembersPulledForwardCount.ShouldBe(0);
        await _groupItemRepository.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
    }

    [Test]
    public async Task EntryDate_AnEndedMembership_IsNotPulledForward_ButCounted()
    {
        _groupItemRepository.GetByClientAndGroup(MemberClientId, GroupId).Returns(new GroupItem
        {
            Id = MemberItemId,
            ClientId = MemberClientId,
            GroupId = GroupId,
            ValidFrom = CompanyToday,
            ValidUntil = CompanyToday.AddYears(1)
        });
        var handler = new AddSelectedClientsToGroupCommandHandler(
            _clientRepository, _groupItemRepository, _unitOfWork, TestGroupWriteVisibility.UnrestrictedGroups(),
            TestGroupWriteVisibility.AllClientsVisible(),
            TestGroupMembershipStart.Planner(_companyClock, _groupItemRepository, Substitute.For<IGroupRepository>(),
                (MemberClientId, new DateOnly(2020, 1, 1))));

        var applied = await handler.Handle(
            Command(apply: true) with { StartMode = GroupMembershipStartMode.EntryDate }, CancellationToken.None);

        applied.MembershipStart!.ExistingMembersPulledForwardCount.ShouldBe(0);
        applied.MembershipStart.ExistingMembersNotPulledForwardCount.ShouldBe(1);
        await _groupItemRepository.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
    }

    [Test]
    public async Task EntryDate_Apply_ReportsTheNumberTheRepositoryActuallyChanged()
    {
        var handler = HandlerWithAnExistingMemberStartingLater();
        _groupItemRepository.PullForwardValidFromAsync(Arg.Any<IReadOnlyDictionary<Guid, DateTime>>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var applied = await handler.Handle(
            Command(apply: true) with { StartMode = GroupMembershipStartMode.EntryDate }, CancellationToken.None);

        applied.MembershipStart!.ExistingMembersPulledForwardCount.ShouldBe(0);
    }
}
