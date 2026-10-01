// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 every ScheduleNotes endpoint (list, get, create, update, delete) ignored group visibility,
/// so a group-restricted user could read and write the schedule notes of any employee. A note owned by a hidden
/// client must now be answered exactly like a missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ScheduleNotes;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ScheduleNotes;

[TestFixture]
public class ScheduleNoteVisibilityTests
{
    private static readonly DateOnly Day = new(2026, 3, 9);

    private IScheduleNoteRepository _repository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduleNoteRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ScheduleNote>>(), Arg.Any<Func<ScheduleNote, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<ScheduleNote>>(0)
                .Where(n => ci.ArgAt<Func<ScheduleNote, Guid>>(1)(n) != _hiddenClientId)
                .ToList());
    }

    [Test]
    public async Task List_LeavesOutNotesOfHiddenClients()
    {
        var visible = NewNote(_visibleClientId);
        var hidden = NewNote(_hiddenClientId);
        _repository.List().Returns(new List<ScheduleNote> { visible, hidden });
        var handler = new ListQueryHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<ListQueryHandler>>());

        var result = (await handler.Handle(new ListQuery<ScheduleNoteResource>(), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visible.Id });
    }

    [Test]
    public async Task Get_NoteOfHiddenClient_AnsweredLikeMissingNote()
    {
        var hidden = NewNote(_hiddenClientId);
        _repository.Get(hidden.Id).Returns(hidden);
        var handler = new GetQueryHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new GetQuery<ScheduleNoteResource>(hidden.Id), CancellationToken.None));

        ex.Message.ShouldBe($"ScheduleNote with ID {hidden.Id} not found");
    }

    [Test]
    public async Task Post_NoteForHiddenClient_RefusedLikeUnknownClient_NothingWritten()
    {
        var resource = NewResource(Guid.NewGuid(), _hiddenClientId);
        var handler = new PostCommandHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<ILogger<PostCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new PostCommand<ScheduleNoteResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_hiddenClientId} not found");
        await _repository.DidNotReceive().Add(Arg.Any<ScheduleNote>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_StoredOwnerHidden_AnsweredLikeMissingNote_NothingWritten()
    {
        var stored = NewNote(_hiddenClientId);
        _repository.GetNoTracking(stored.Id).Returns(stored);
        var handler = NewPutHandler();

        var result = await handler.Handle(
            new PutCommand<ScheduleNoteResource>(NewResource(stored.Id, _visibleClientId)), CancellationToken.None);

        result.ShouldBeNull();
        await AssertNothingPutAsync();
    }

    [Test]
    public async Task Put_NewOwnerHidden_AnsweredLikeMissingNote_NothingWritten()
    {
        var stored = NewNote(_visibleClientId);
        _repository.GetNoTracking(stored.Id).Returns(stored);
        var handler = NewPutHandler();

        var result = await handler.Handle(
            new PutCommand<ScheduleNoteResource>(NewResource(stored.Id, _hiddenClientId)), CancellationToken.None);

        result.ShouldBeNull();
        await AssertNothingPutAsync();
    }

    [Test]
    public async Task Delete_NoteOfHiddenClient_AnsweredLikeMissingNote_NothingDeleted()
    {
        var hidden = NewNote(_hiddenClientId);
        _repository.Get(hidden.Id).Returns(hidden);
        var handler = new DeleteCommandHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<ILogger<DeleteCommandHandler>>());

        var result = await handler.Handle(new DeleteCommand<ScheduleNoteResource>(hidden.Id), CancellationToken.None);

        result.ShouldBeNull();
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private PutCommandHandler NewPutHandler()
    {
        return new PutCommandHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    private async Task AssertNothingPutAsync()
    {
        await _repository.DidNotReceive().Put(Arg.Any<ScheduleNote>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private static ScheduleNote NewNote(Guid clientId)
    {
        return new ScheduleNote { Id = Guid.NewGuid(), ClientId = clientId, CurrentDate = Day, Content = "Note" };
    }

    private static ScheduleNoteResource NewResource(Guid id, Guid clientId)
    {
        return new ScheduleNoteResource { Id = id, ClientId = clientId, CurrentDate = Day, Content = "Note" };
    }
}
