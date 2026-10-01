// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Client notes are client personal data and their write endpoints are open to every authenticated user.
/// Until 2026-10-01 none of the annotation handlers consulted group visibility, so a group-restricted user
/// could list the notes of the whole tenant and read, create, move or delete notes of any client whose id
/// they knew. A note owned by a hidden client must now be answered exactly like a missing note, and a
/// refused write must not touch the repository. The real ClientVisibilityGuard runs over a substituted
/// search repository, so the tests also prove which client ids the handlers ask about.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Annotations;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Annotation;
using Klacks.Api.Application.Services.Clients;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Annotations;

[TestFixture]
public class AnnotationHandlersVisibilityTests
{
    private readonly HashSet<Guid> _visibleClientIds = [];
    private IAnnotationRepository _annotationRepository = null!;
    private IClientSearchRepository _clientSearchRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SettingsMapper _mapper = null!;

    [SetUp]
    public void SetUp()
    {
        _visibleClientIds.Clear();
        _annotationRepository = Substitute.For<IAnnotationRepository>();
        _clientSearchRepository = Substitute.For<IClientSearchRepository>();
        _clientSearchRepository
            .IsVisibleToCallerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_visibleClientIds.Contains(call.ArgAt<Guid>(0))));
        _clientSearchRepository
            .FilterVisibleToCallerAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlySet<Guid>>(
                call.ArgAt<IReadOnlyCollection<Guid>>(0).Where(_visibleClientIds.Contains).ToHashSet()));
        _clientVisibilityGuard = new ClientVisibilityGuard(_clientSearchRepository, Substitute.For<Klacks.Api.Domain.Services.Common.IClientGroupFilterService>());
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mapper = new SettingsMapper();
    }

    [Test]
    public async Task List_ReturnsOnlyNotesOfVisibleClients()
    {
        var visibleClientId = VisibleClient();
        var hiddenClientId = Guid.NewGuid();
        var visibleNote = Note(visibleClientId);
        _annotationRepository.List().Returns(new List<Annotation> { visibleNote, Note(hiddenClientId) });
        var handler = new ListQueryHandler(_annotationRepository, _clientVisibilityGuard, _mapper);

        var result = (await handler.Handle(new ListQuery<AnnotationResource>(), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visibleNote.Id });
    }

    [Test]
    public async Task Get_NoteOfVisibleClient_IsReturned()
    {
        var note = Note(VisibleClient());
        _annotationRepository.Get(note.Id).Returns(note);

        var result = await CreateGetHandler().Handle(new GetQuery<AnnotationResource>(note.Id), CancellationToken.None);

        result.Id.ShouldBe(note.Id);
    }

    [Test]
    public async Task Get_NoteOfHiddenClient_IsAnsweredLikeAMissingNote()
    {
        var hiddenNote = Note(Guid.NewGuid());
        var missingId = Guid.NewGuid();
        _annotationRepository.Get(hiddenNote.Id).Returns(hiddenNote);
        var handler = CreateGetHandler();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<AnnotationResource>(hiddenNote.Id), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<AnnotationResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe($"Annotation with ID {hiddenNote.Id} not found");
        missing.Message.ShouldBe($"Annotation with ID {missingId} not found");
    }

    [Test]
    public async Task SimpleList_VisibleClient_ReturnsItsNotes()
    {
        var clientId = VisibleClient();
        var note = Note(clientId);
        _annotationRepository.SimpleList(clientId).Returns(new List<Annotation> { note });

        var result = (await CreateSimpleListHandler().Handle(new GetSimpleListQuery(clientId), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { note.Id });
    }

    [Test]
    public async Task SimpleList_HiddenClient_ReturnsEmptyListWithoutReadingNotes()
    {
        var hiddenClientId = Guid.NewGuid();
        _annotationRepository.SimpleList(hiddenClientId).Returns(new List<Annotation> { Note(hiddenClientId) });

        var result = await CreateSimpleListHandler().Handle(new GetSimpleListQuery(hiddenClientId), CancellationToken.None);

        result.ShouldBeEmpty();
        await _annotationRepository.DidNotReceive().SimpleList(Arg.Any<Guid>());
    }

    [Test]
    public async Task Post_VisibleClient_IsWritten()
    {
        var resource = new AnnotationResource { ClientId = VisibleClient(), Note = "visible" };

        var result = await CreatePostHandler().Handle(new PostCommand<AnnotationResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _annotationRepository.Received(1).Add(Arg.Any<Annotation>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Post_HiddenClient_IsRefusedLikeAMissingClientAndNothingIsWritten()
    {
        var hiddenClientId = Guid.NewGuid();
        var resource = new AnnotationResource { ClientId = hiddenClientId, Note = "hidden" };

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePostHandler().Handle(new PostCommand<AnnotationResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe($"Client with ID {hiddenClientId} not found");
        await _annotationRepository.DidNotReceive().Add(Arg.Any<Annotation>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_StoredAndIncomingOwnerVisible_IsWritten()
    {
        var clientId = VisibleClient();
        var stored = Note(clientId);
        _annotationRepository.Get(stored.Id).Returns(stored);
        var resource = new AnnotationResource { Id = stored.Id, ClientId = clientId, Note = "changed" };

        var result = await CreatePutHandler().Handle(new PutCommand<AnnotationResource>(resource), CancellationToken.None);

        result!.Note.ShouldBe("changed");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_StoredOwnerHidden_IsRefusedLikeAMissingNoteAndNothingIsWritten()
    {
        var stored = Note(Guid.NewGuid());
        _annotationRepository.Get(stored.Id).Returns(stored);
        var resource = new AnnotationResource { Id = stored.Id, ClientId = VisibleClient(), Note = "moved away" };

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePutHandler().Handle(new PutCommand<AnnotationResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe(MissingNoteMessage(stored.Id));
        stored.Note.ShouldNotBe("moved away");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_IncomingOwnerHidden_IsRefusedLikeAMissingNoteAndNothingIsWritten()
    {
        var visibleClientId = VisibleClient();
        var stored = Note(visibleClientId);
        _annotationRepository.Get(stored.Id).Returns(stored);
        var resource = new AnnotationResource { Id = stored.Id, ClientId = Guid.NewGuid(), Note = "moved onto hidden" };

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePutHandler().Handle(new PutCommand<AnnotationResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe(MissingNoteMessage(stored.Id));
        stored.ClientId.ShouldBe(visibleClientId);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_MissingNote_UsesTheSameMessage()
    {
        var resource = new AnnotationResource { Id = Guid.NewGuid(), ClientId = VisibleClient() };

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => CreatePutHandler().Handle(new PutCommand<AnnotationResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe(MissingNoteMessage(resource.Id));
    }

    [Test]
    public async Task Delete_NoteOfVisibleClient_IsDeleted()
    {
        var note = Note(VisibleClient());
        _annotationRepository.Get(note.Id).Returns(note);

        var result = await CreateDeleteHandler().Handle(new DeleteCommand<AnnotationResource>(note.Id), CancellationToken.None);

        result!.Id.ShouldBe(note.Id);
        await _annotationRepository.Received(1).Delete(note.Id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Delete_NoteOfHiddenClient_IsRefusedLikeAMissingNoteAndNothingIsDeleted()
    {
        var hiddenNote = Note(Guid.NewGuid());
        var missingId = Guid.NewGuid();
        _annotationRepository.Get(hiddenNote.Id).Returns(hiddenNote);
        var handler = CreateDeleteHandler();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new DeleteCommand<AnnotationResource>(hiddenNote.Id), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new DeleteCommand<AnnotationResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe(MissingNoteMessage(hiddenNote.Id));
        missing.Message.ShouldBe(MissingNoteMessage(missingId));
        await _annotationRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private static string MissingNoteMessage(Guid id) => $"Annotation with ID {id} not found.";

    private static Annotation Note(Guid clientId) => new() { Id = Guid.NewGuid(), ClientId = clientId, Note = "note" };

    private Guid VisibleClient()
    {
        var clientId = Guid.NewGuid();
        _visibleClientIds.Add(clientId);
        return clientId;
    }

    private GetQueryHandler CreateGetHandler() =>
        new(_annotationRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<GetQueryHandler>>());

    private GetSimpleQueryHandler CreateSimpleListHandler() =>
        new(_annotationRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<GetSimpleQueryHandler>>());

    private PostCommandHandler CreatePostHandler() =>
        new(_annotationRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler CreatePutHandler() =>
        new(_annotationRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler CreateDeleteHandler() =>
        new(_annotationRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<DeleteCommandHandler>>());
}
