// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The BreakPlaceholders get, create, update and delete endpoints are open to every authenticated caller
/// by design. Until 2026-10-01 they ignored group visibility, so a placeholder of any employee could be
/// read, written or moved onto any employee. A placeholder owned by a hidden client must now be answered
/// exactly like a missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.BreakPlaceholders;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.BreakPlaceholders;

[TestFixture]
public class BreakPlaceholderVisibilityTests
{
    private IBreakPlaceholderRepository _repository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IBreakPlaceholderRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
    }

    [Test]
    public async Task Get_PlaceholderOfHiddenClient_IsAnsweredLikeAMissingPlaceholder()
    {
        var hidden = NewPlaceholder(_hiddenClientId);
        var missingId = Guid.NewGuid();
        _repository.Get(hidden.Id).Returns(hidden);
        _repository.Get(missingId).Returns((BreakPlaceholder?)null);
        var handler = new GetQueryHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var hiddenEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<BreakPlaceholderResource>(hidden.Id), CancellationToken.None));
        var missingEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<BreakPlaceholderResource>(missingId), CancellationToken.None));

        hiddenEx.Message.ShouldBe($"Break with ID {hidden.Id} not found");
        missingEx.Message.ShouldBe($"Break with ID {missingId} not found");
    }

    [Test]
    public async Task Post_ForHiddenClient_IsRefusedLikeAMissingClient_NothingWritten()
    {
        var handler = new PostCommandHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<ILogger<PostCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PostCommand<BreakPlaceholderResource>(NewResource(Guid.NewGuid(), _hiddenClientId)),
            CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_hiddenClientId} not found");
        await _repository.DidNotReceive().Add(Arg.Any<BreakPlaceholder>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_OfAPlaceholderOwnedByAHiddenClient_IsRefusedLikeAMissingPlaceholder_NothingWritten()
    {
        var stored = NewPlaceholder(_hiddenClientId);
        _repository.Get(stored.Id).Returns(stored);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => NewPutHandler().Handle(
            new PutCommand<BreakPlaceholderResource>(NewResource(stored.Id, _visibleClientId)),
            CancellationToken.None));

        ex.Message.ShouldBe($"Break with ID {stored.Id} not found.");
        stored.ClientId.ShouldBe(_hiddenClientId);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_MovingAVisiblePlaceholderOntoAHiddenClient_IsRefusedLikeAMissingPlaceholder_NothingWritten()
    {
        var stored = NewPlaceholder(_visibleClientId);
        _repository.Get(stored.Id).Returns(stored);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => NewPutHandler().Handle(
            new PutCommand<BreakPlaceholderResource>(NewResource(stored.Id, _hiddenClientId)),
            CancellationToken.None));

        ex.Message.ShouldBe($"Break with ID {stored.Id} not found.");
        stored.ClientId.ShouldBe(_visibleClientId);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_PlaceholderOfHiddenClient_IsRefusedLikeAMissingPlaceholder_NothingDeleted()
    {
        var hidden = NewPlaceholder(_hiddenClientId);
        _repository.Get(hidden.Id).Returns(hidden);
        var handler = new DeleteCommandHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<ILogger<DeleteCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new DeleteCommand<BreakPlaceholderResource>(hidden.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Break with ID {hidden.Id} not found.");
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private PutCommandHandler NewPutHandler()
        => new(_repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<ILogger<PutCommandHandler>>());

    private static BreakPlaceholder NewPlaceholder(Guid clientId)
        => new() { Id = Guid.NewGuid(), ClientId = clientId, AbsenceId = Guid.NewGuid() };

    private static BreakPlaceholderResource NewResource(Guid id, Guid clientId)
        => new() { Id = id, ClientId = clientId, AbsenceId = Guid.NewGuid() };
}
