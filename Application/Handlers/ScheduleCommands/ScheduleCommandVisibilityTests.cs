// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-04 the ScheduleCommands endpoints (list, get, create, update, delete) ignored group
/// visibility, so a group-restricted planner could list every client's FREE/NIGHT commands and create,
/// alter or delete them for clients outside their groups. A command owned by a hidden client must now be
/// answered exactly like a missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ScheduleCommands;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ScheduleCommands;

[TestFixture]
public class ScheduleCommandVisibilityTests
{
    private const string FreeKeyword = "FREE";
    private static readonly DateOnly Day = new(2026, 3, 9);

    private IScheduleCommandRepository _repository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduleCommandRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();

        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ScheduleCommand>>(), Arg.Any<Func<ScheduleCommand, Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<ScheduleCommand>>(0)
                .Where(c => ci.ArgAt<Func<ScheduleCommand, Guid>>(1)(c) != _hiddenClientId)
                .ToList());
    }

    [Test]
    public async Task List_LeavesOutCommandsOfHiddenClients()
    {
        var visible = NewCommand(_visibleClientId);
        var hidden = NewCommand(_hiddenClientId);
        _repository.List().Returns(new List<ScheduleCommand> { visible, hidden });
        var handler = new ListQueryHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<ListQueryHandler>>());

        var result = (await handler.Handle(new ListQuery<ScheduleCommandResource>(), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visible.Id });
    }

    [Test]
    public async Task Get_CommandOfHiddenClient_IsAnsweredLikeAMissingCommand()
    {
        var hidden = NewCommand(_hiddenClientId);
        var missingId = Guid.NewGuid();
        _repository.Get(hidden.Id).Returns(hidden);
        _repository.Get(missingId).Returns((ScheduleCommand?)null);
        var handler = new GetQueryHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var hiddenEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<ScheduleCommandResource>(hidden.Id), CancellationToken.None));
        var missingEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<ScheduleCommandResource>(missingId), CancellationToken.None));

        hiddenEx.Message.ShouldBe($"ScheduleCommand with ID {hidden.Id} not found");
        missingEx.Message.ShouldBe($"ScheduleCommand with ID {missingId} not found");
    }

    [Test]
    public async Task Get_CommandOfVisibleClient_IsReturned()
    {
        var visible = NewCommand(_visibleClientId);
        _repository.Get(visible.Id).Returns(visible);
        var handler = new GetQueryHandler(
            _repository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var result = await handler.Handle(new GetQuery<ScheduleCommandResource>(visible.Id), CancellationToken.None);

        result.Id.ShouldBe(visible.Id);
    }

    [Test]
    public async Task Post_ForHiddenClient_IsRefusedLikeAMissingClient_NothingWritten()
    {
        var handler = NewPostHandler();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PostCommand<ScheduleCommandResource>(NewResource(Guid.NewGuid(), _hiddenClientId)),
            CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_hiddenClientId} not found");
        await _repository.DidNotReceive().Add(Arg.Any<ScheduleCommand>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_ForVisibleClient_IsWritten()
    {
        var handler = NewPostHandler();

        var result = await handler.Handle(
            new PostCommand<ScheduleCommandResource>(NewResource(Guid.NewGuid(), _visibleClientId)),
            CancellationToken.None);

        result.ShouldNotBeNull();
        await _repository.Received(1).Add(Arg.Is<ScheduleCommand>(c => c.ClientId == _visibleClientId));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_OfACommandOwnedByAHiddenClient_IsAnsweredLikeAMissingCommand_NothingWritten()
    {
        var stored = NewCommand(_hiddenClientId);
        _repository.GetNoTracking(stored.Id).Returns(stored);
        var handler = NewPutHandler();

        var result = await handler.Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, _visibleClientId)), CancellationToken.None);

        result.ShouldBeNull();
        await _repository.DidNotReceive().Put(Arg.Any<ScheduleCommand>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_MovingAVisibleCommandOntoAHiddenClient_IsAnsweredLikeAMissingCommand_NothingWritten()
    {
        var stored = NewCommand(_visibleClientId);
        _repository.GetNoTracking(stored.Id).Returns(stored);
        var handler = NewPutHandler();

        var result = await handler.Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, _hiddenClientId)), CancellationToken.None);

        result.ShouldBeNull();
        await _repository.DidNotReceive().Put(Arg.Any<ScheduleCommand>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_OfAVisibleCommand_IsWritten()
    {
        var stored = NewCommand(_visibleClientId);
        _repository.GetNoTracking(stored.Id).Returns(stored);
        _repository.Put(Arg.Any<ScheduleCommand>()).Returns(ci => ci.Arg<ScheduleCommand>());
        var handler = NewPutHandler();

        var result = await handler.Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, _visibleClientId)), CancellationToken.None);

        result.ShouldNotBeNull();
        await _repository.Received(1).Put(Arg.Any<ScheduleCommand>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_WithAForgedScenarioToken_KeepsTheStoredMainPlanToken()
    {
        var stored = NewCommand(_visibleClientId);
        _repository.GetNoTracking(stored.Id).Returns(stored);
        _repository.Put(Arg.Any<ScheduleCommand>()).Returns(ci => ci.Arg<ScheduleCommand>());
        var resource = NewResource(stored.Id, _visibleClientId);
        resource.AnalyseToken = Guid.NewGuid();

        await NewPutHandler().Handle(new PutCommand<ScheduleCommandResource>(resource), CancellationToken.None);

        await _repository.Received(1).Put(Arg.Is<ScheduleCommand>(c => c.AnalyseToken == null));
    }

    [Test]
    public async Task Put_OfAScenarioCommand_KeepsTheScenarioTokenWhenThePayloadDropsIt()
    {
        var token = Guid.NewGuid();
        var stored = NewCommand(_visibleClientId);
        stored.AnalyseToken = token;
        _repository.GetNoTracking(stored.Id).Returns(stored);
        _repository.Put(Arg.Any<ScheduleCommand>()).Returns(ci => ci.Arg<ScheduleCommand>());

        await NewPutHandler().Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, _visibleClientId)), CancellationToken.None);

        await _repository.Received(1).Put(Arg.Is<ScheduleCommand>(c => c.AnalyseToken == token));
    }

    [Test]
    public async Task Delete_CommandOfHiddenClient_IsAnsweredLikeAMissingCommand_NothingDeleted()
    {
        var hidden = NewCommand(_hiddenClientId);
        _repository.Get(hidden.Id).Returns(hidden);
        var handler = NewDeleteHandler();

        var result = await handler.Handle(
            new DeleteCommand<ScheduleCommandResource>(hidden.Id), CancellationToken.None);

        result.ShouldBeNull();
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_CommandOfVisibleClient_IsDeleted()
    {
        var visible = NewCommand(_visibleClientId);
        _repository.Get(visible.Id).Returns(visible);
        var handler = NewDeleteHandler();

        var result = await handler.Handle(
            new DeleteCommand<ScheduleCommandResource>(visible.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _repository.Received(1).Delete(visible.Id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    private PostCommandHandler NewPostHandler() => new(
        _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
        Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler() => new(
        _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
        Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler NewDeleteHandler() => new(
        _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
        Substitute.For<ILogger<DeleteCommandHandler>>());

    private static ScheduleCommand NewCommand(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = Day,
        CommandKeyword = FreeKeyword
    };

    private static ScheduleCommandResource NewResource(Guid id, Guid clientId) => new()
    {
        Id = id,
        ClientId = clientId,
        CurrentDate = Day,
        CommandKeyword = FreeKeyword
    };
}
