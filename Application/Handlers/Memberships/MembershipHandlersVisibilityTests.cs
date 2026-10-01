// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Membership reads and writes must respect the group visibility of the owning client: a membership of a
/// hidden client is answered exactly like a missing membership, the tenant-wide list leaves it out, and no
/// write touches the repository or commits when the stored or the requested owner is hidden.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Microsoft.Extensions.Logging;
using MembershipDeleteHandler = Klacks.Api.Application.Handlers.Memberships.DeleteCommandHandler;
using MembershipGetHandler = Klacks.Api.Application.Handlers.Memberships.GetQueryHandler;
using MembershipListHandler = Klacks.Api.Application.Handlers.Memberships.GetListQueryHandler;
using MembershipPostHandler = Klacks.Api.Application.Handlers.Memberships.PostCommandHandler;
using MembershipPutHandler = Klacks.Api.Application.Handlers.Memberships.PutCommandHandler;

namespace Klacks.UnitTest.Application.Handlers.Memberships;

[TestFixture]
public class MembershipHandlersVisibilityTests
{
    private static readonly DateTime ValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IMembershipRepository _membershipRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ScheduleMapper _mapper = null!;

    [SetUp]
    public void SetUp()
    {
        _membershipRepository = Substitute.For<IMembershipRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mapper = new ScheduleMapper();
    }

    private static Membership MembershipOf(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ValidFrom = ValidFrom
    };

    private static MembershipResource ResourceOf(Guid id, Guid clientId) => new()
    {
        Id = id,
        ClientId = clientId,
        ValidFrom = ValidFrom
    };

    private void ClientIsVisible(Guid clientId, bool visible)
    {
        _clientVisibilityGuard.IsVisibleAsync(clientId, Arg.Any<CancellationToken>()).Returns(visible);
    }

    private async Task NothingWasWrittenAsync()
    {
        await _membershipRepository.DidNotReceive().Add(Arg.Any<Membership>());
        await _membershipRepository.DidNotReceive().Put(Arg.Any<Membership>());
        await _membershipRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Get_MembershipOfHiddenClient_IsAnsweredLikeAMissingMembership()
    {
        var hiddenClientId = Guid.NewGuid();
        var membership = MembershipOf(hiddenClientId);
        var missingId = Guid.NewGuid();
        _membershipRepository.Get(membership.Id).Returns(membership);
        ClientIsVisible(hiddenClientId, false);
        var handler = new MembershipGetHandler(_membershipRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<MembershipGetHandler>>());

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<MembershipResource>(membership.Id), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<MembershipResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe($"Membership with ID {membership.Id} not found");
        missing.Message.ShouldBe($"Membership with ID {missingId} not found");
    }

    [Test]
    public async Task Get_MembershipOfVisibleClient_IsReturned()
    {
        var clientId = Guid.NewGuid();
        var membership = MembershipOf(clientId);
        _membershipRepository.Get(membership.Id).Returns(membership);
        ClientIsVisible(clientId, true);
        var handler = new MembershipGetHandler(_membershipRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<MembershipGetHandler>>());

        var result = await handler.Handle(new GetQuery<MembershipResource>(membership.Id), CancellationToken.None);

        result.Id.ShouldBe(membership.Id);
    }

    [Test]
    public async Task List_LeavesOutMembershipsOfHiddenClients()
    {
        var visible = MembershipOf(Guid.NewGuid());
        var hidden = MembershipOf(Guid.NewGuid());
        _membershipRepository.List().Returns(new List<Membership> { visible, hidden });
        _clientVisibilityGuard
            .FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Membership>>(), Arg.Any<Func<Membership, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Membership>>()
                .Where(m => call.Arg<Func<Membership, Guid>>()(m) == visible.ClientId)
                .ToList());
        var handler = new MembershipListHandler(_membershipRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<MembershipListHandler>>());

        var result = (await handler.Handle(new ListQuery<MembershipResource>(), CancellationToken.None)).ToList();

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(visible.Id);
    }

    [Test]
    public async Task Post_ForHiddenClient_IsRefusedLikeAMissingClientAndWritesNothing()
    {
        var hiddenClientId = Guid.NewGuid();
        ClientIsVisible(hiddenClientId, false);
        var handler = new MembershipPostHandler(_membershipRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<MembershipPostHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PostCommand<MembershipResource>(ResourceOf(Guid.Empty, hiddenClientId)), CancellationToken.None));

        exception.Message.ShouldBe($"Client with ID {hiddenClientId} not found");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Post_ForVisibleClient_IsWritten()
    {
        var clientId = Guid.NewGuid();
        ClientIsVisible(clientId, true);
        var handler = new MembershipPostHandler(_membershipRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<MembershipPostHandler>>());

        await handler.Handle(new PostCommand<MembershipResource>(ResourceOf(Guid.Empty, clientId)), CancellationToken.None);

        await _membershipRepository.Received(1).Add(Arg.Any<Membership>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_MembershipWhoseStoredOwnerIsHidden_IsRefusedLikeAMissingMembership()
    {
        var hiddenClientId = Guid.NewGuid();
        var visibleClientId = Guid.NewGuid();
        var stored = MembershipOf(hiddenClientId);
        _membershipRepository.Get(stored.Id).Returns(stored);
        _clientVisibilityGuard
            .AreAllVisibleAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hiddenClientId)), Arg.Any<CancellationToken>())
            .Returns(false);
        var handler = new MembershipPutHandler(_membershipRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<MembershipPutHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PutCommand<MembershipResource>(ResourceOf(stored.Id, visibleClientId)), CancellationToken.None));

        exception.Message.ShouldBe($"Membership with ID {stored.Id} not found.");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Put_MovingAMembershipOntoAHiddenClient_IsRefusedLikeAMissingMembership()
    {
        var visibleClientId = Guid.NewGuid();
        var hiddenClientId = Guid.NewGuid();
        var stored = MembershipOf(visibleClientId);
        _membershipRepository.Get(stored.Id).Returns(stored);
        _clientVisibilityGuard
            .AreAllVisibleAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hiddenClientId)), Arg.Any<CancellationToken>())
            .Returns(false);
        var handler = new MembershipPutHandler(_membershipRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<MembershipPutHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PutCommand<MembershipResource>(ResourceOf(stored.Id, hiddenClientId)), CancellationToken.None));

        exception.Message.ShouldBe($"Membership with ID {stored.Id} not found.");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Put_WithinVisibleClient_IsWritten()
    {
        var clientId = Guid.NewGuid();
        var stored = MembershipOf(clientId);
        _membershipRepository.Get(stored.Id).Returns(stored);
        _clientVisibilityGuard
            .AreAllVisibleAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.All(id => id == clientId)), Arg.Any<CancellationToken>())
            .Returns(true);
        var handler = new MembershipPutHandler(_membershipRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<MembershipPutHandler>>());

        await handler.Handle(new PutCommand<MembershipResource>(ResourceOf(stored.Id, clientId)), CancellationToken.None);

        await _membershipRepository.Received(1).Put(Arg.Any<Membership>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Delete_MembershipOfHiddenClient_IsRefusedLikeAMissingMembership()
    {
        var hiddenClientId = Guid.NewGuid();
        var stored = MembershipOf(hiddenClientId);
        _membershipRepository.Get(stored.Id).Returns(stored);
        ClientIsVisible(hiddenClientId, false);
        var handler = new MembershipDeleteHandler(_membershipRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<MembershipDeleteHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new DeleteCommand<MembershipResource>(stored.Id), CancellationToken.None));

        exception.Message.ShouldBe($"Membership with ID {stored.Id} not found.");
        await NothingWasWrittenAsync();
    }
}
