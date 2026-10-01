// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Communication entries (phone numbers, mail addresses) are client personal data. Until 2026-10-01 none of
/// the communication handlers consulted group visibility, so a group-restricted caller could list the
/// entries of the whole tenant and read, create, move or delete entries of any client whose id they knew.
/// An entry owned by a hidden client must now be answered exactly like a missing entry, and a refused
/// write must neither touch the repository nor run the inbox mail (re)assignment. The real
/// ClientVisibilityGuard runs over a substituted search repository, so the tests also prove which client
/// ids the handlers ask about.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Handlers.Communications;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Interfaces.Email;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Communications;

[TestFixture]
public class CommunicationHandlersVisibilityTests
{
    private readonly HashSet<Guid> _visibleClientIds = [];
    private ICommunicationRepository _communicationRepository = null!;
    private IClientSearchRepository _clientSearchRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IEmailClientAssignmentService _emailAssignmentService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AddressCommunicationMapper _mapper = null!;

    [SetUp]
    public void SetUp()
    {
        _visibleClientIds.Clear();
        _communicationRepository = Substitute.For<ICommunicationRepository>();
        _clientSearchRepository = Substitute.For<IClientSearchRepository>();
        _clientSearchRepository
            .IsVisibleToCallerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_visibleClientIds.Contains(call.ArgAt<Guid>(0))));
        _clientSearchRepository
            .FilterVisibleToCallerAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlySet<Guid>>(
                call.ArgAt<IReadOnlyCollection<Guid>>(0).Where(_visibleClientIds.Contains).ToHashSet()));
        _clientVisibilityGuard = new ClientVisibilityGuard(_clientSearchRepository, Substitute.For<Klacks.Api.Domain.Services.Common.IClientGroupFilterService>());
        _emailAssignmentService = Substitute.For<IEmailClientAssignmentService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mapper = new AddressCommunicationMapper();
    }

    [Test]
    public async Task List_ReturnsOnlyEntriesOfVisibleClients()
    {
        var visibleEntry = Entry(VisibleClient());
        _communicationRepository.List().Returns(new List<Communication> { visibleEntry, Entry(Guid.NewGuid()) });
        var handler = new GetListQueryHandler(_communicationRepository, _clientVisibilityGuard, _mapper);

        var result = (await handler.Handle(new ListQuery<CommunicationResource>(), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visibleEntry.Id });
    }

    [Test]
    public async Task Get_EntryOfVisibleClient_IsReturned()
    {
        var entry = Entry(VisibleClient());
        _communicationRepository.Get(entry.Id).Returns(entry);

        var result = await CreateGetHandler().Handle(new GetQuery<CommunicationResource>(entry.Id), CancellationToken.None);

        result.Id.ShouldBe(entry.Id);
    }

    [Test]
    public async Task Get_EntryOfHiddenClient_IsAnsweredLikeAMissingEntry()
    {
        var hiddenEntry = Entry(Guid.NewGuid());
        var missingId = Guid.NewGuid();
        _communicationRepository.Get(hiddenEntry.Id).Returns(hiddenEntry);
        var handler = CreateGetHandler();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<CommunicationResource>(hiddenEntry.Id), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<CommunicationResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe($"Communication with ID {hiddenEntry.Id} not found");
        missing.Message.ShouldBe($"Communication with ID {missingId} not found");
    }

    [Test]
    public async Task Post_VisibleClient_IsWrittenAndAssignsInboxMails()
    {
        var resource = Resource(Guid.NewGuid(), VisibleClient());

        var result = await CreatePostHandler().Handle(new PostCommand<CommunicationResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _communicationRepository.Received(1).Add(Arg.Any<Communication>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _emailAssignmentService.Received(1).AssignInboxEmailsToClientsAsync();
    }

    [Test]
    public async Task Post_HiddenClient_IsRefusedLikeAMissingClientWithoutSideEffects()
    {
        var hiddenClientId = Guid.NewGuid();
        var resource = Resource(Guid.NewGuid(), hiddenClientId);

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePostHandler().Handle(new PostCommand<CommunicationResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe($"Client with ID {hiddenClientId} not found");
        await _communicationRepository.DidNotReceive().Add(Arg.Any<Communication>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _emailAssignmentService.DidNotReceive().AssignInboxEmailsToClientsAsync();
    }

    [Test]
    public async Task Put_StoredAndIncomingOwnerVisible_IsWrittenAndAssignsInboxMails()
    {
        var clientId = VisibleClient();
        var stored = Entry(clientId);
        _communicationRepository.GetNoTracking(stored.Id).Returns(stored);

        var result = await CreatePutHandler().Handle(
            new PutCommand<CommunicationResource>(Resource(stored.Id, clientId)), CancellationToken.None);

        result.ShouldNotBeNull();
        await _communicationRepository.Received(1).Put(Arg.Any<Communication>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _emailAssignmentService.Received(1).AssignInboxEmailsToClientsAsync();
    }

    [Test]
    public async Task Put_StoredOwnerHidden_IsRefusedLikeAMissingEntryWithoutSideEffects()
    {
        var stored = Entry(Guid.NewGuid());
        _communicationRepository.GetNoTracking(stored.Id).Returns(stored);

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePutHandler().Handle(
                new PutCommand<CommunicationResource>(Resource(stored.Id, VisibleClient())), CancellationToken.None));

        exception.Message.ShouldBe(MissingEntryMessage(stored.Id));
        await AssertNoPutSideEffects();
    }

    [Test]
    public async Task Put_IncomingOwnerHidden_IsRefusedLikeAMissingEntryWithoutSideEffects()
    {
        var stored = Entry(VisibleClient());
        _communicationRepository.GetNoTracking(stored.Id).Returns(stored);

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePutHandler().Handle(
                new PutCommand<CommunicationResource>(Resource(stored.Id, Guid.NewGuid())), CancellationToken.None));

        exception.Message.ShouldBe(MissingEntryMessage(stored.Id));
        await AssertNoPutSideEffects();
    }

    [Test]
    public async Task Put_MissingEntry_UsesTheSameMessage()
    {
        var resource = Resource(Guid.NewGuid(), VisibleClient());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePutHandler().Handle(new PutCommand<CommunicationResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe(MissingEntryMessage(resource.Id));
    }

    [Test]
    public async Task Delete_EntryOfVisibleClient_IsDeletedAndReassignsOrphanedMails()
    {
        var entry = Entry(VisibleClient());
        _communicationRepository.Get(entry.Id).Returns(entry);

        var result = await CreateDeleteHandler().Handle(new DeleteCommand<CommunicationResource>(entry.Id), CancellationToken.None);

        result!.Id.ShouldBe(entry.Id);
        await _communicationRepository.Received(1).Delete(entry.Id);
        await _unitOfWork.Received(1).CompleteAsync();
        await _emailAssignmentService.Received(1).ReassignOrphanedEmailsAsync();
    }

    [Test]
    public async Task Delete_EntryOfHiddenClient_IsRefusedLikeAMissingEntryWithoutSideEffects()
    {
        var hiddenEntry = Entry(Guid.NewGuid());
        var missingId = Guid.NewGuid();
        _communicationRepository.Get(hiddenEntry.Id).Returns(hiddenEntry);
        var handler = CreateDeleteHandler();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new DeleteCommand<CommunicationResource>(hiddenEntry.Id), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new DeleteCommand<CommunicationResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe(MissingEntryMessage(hiddenEntry.Id));
        missing.Message.ShouldBe(MissingEntryMessage(missingId));
        await _communicationRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _emailAssignmentService.DidNotReceive().ReassignOrphanedEmailsAsync();
    }

    private async Task AssertNoPutSideEffects()
    {
        await _communicationRepository.DidNotReceive().Put(Arg.Any<Communication>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _emailAssignmentService.DidNotReceive().AssignInboxEmailsToClientsAsync();
    }

    private static string MissingEntryMessage(Guid id) => $"Communication with ID {id} not found.";

    private static Communication Entry(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Type = CommunicationTypeEnum.PrivateMail,
        Value = "client@test.com"
    };

    private static CommunicationResource Resource(Guid id, Guid clientId) => new()
    {
        Id = id,
        ClientId = clientId,
        Type = CommunicationTypeEnum.PrivateMail,
        Value = "client@test.com"
    };

    private Guid VisibleClient()
    {
        var clientId = Guid.NewGuid();
        _visibleClientIds.Add(clientId);
        return clientId;
    }

    private GetQueryHandler CreateGetHandler() =>
        new(_communicationRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<GetQueryHandler>>());

    private PostCommandHandler CreatePostHandler() =>
        new(_communicationRepository, _clientVisibilityGuard, _mapper, _unitOfWork, _emailAssignmentService,
            Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler CreatePutHandler() =>
        new(_communicationRepository, _clientVisibilityGuard, _mapper, _unitOfWork, _emailAssignmentService,
            Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler CreateDeleteHandler() =>
        new(_communicationRepository, _clientVisibilityGuard, _emailAssignmentService, _mapper, _unitOfWork,
            Substitute.For<ILogger<DeleteCommandHandler>>());
}
