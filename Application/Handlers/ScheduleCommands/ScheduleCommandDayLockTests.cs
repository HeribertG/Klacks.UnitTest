// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-12 the ScheduleCommands create/update/delete handlers ignored the day lock, so a planner could
/// place, alter or remove FREE/EARLY/... commands on sealed days although Expenses, WorkChanges and Breaks were already refused there. Every write
/// on a sealed day must now be refused with the IDayLockService error and nothing may be written; for an update
/// both the stored and the new (date, client) pair count, so a command can neither leave nor enter a sealed day.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ScheduleCommands;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.ScheduleCommands;

[TestFixture]
public class ScheduleCommandDayLockTests
{
    private const string FreeKeyword = "FREE";
    private const string SealedMessage = "Day is sealed and cannot be modified.";
    private static readonly DateOnly SealedDay = new(2026, 3, 9);
    private static readonly DateOnly OpenDay = new(2026, 3, 10);

    private IScheduleCommandRepository _repository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IScheduleRowAccessGuard _rowAccessGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDayLockService _dayLockService = null!;
    private Guid _clientId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduleCommandRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _rowAccessGuard = TestScheduleRowAccess.Over(_clientVisibilityGuard);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _dayLockService = Substitute.For<IDayLockService>();
        _clientId = Guid.NewGuid();

        _dayLockService
            .When(s => s.EnsureNotLockedAsync(SealedDay, Arg.Any<Guid>(), null, Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidRequestException(SealedMessage));
        _repository.Put(Arg.Any<ScheduleCommand>()).Returns(ci => ci.Arg<ScheduleCommand>());
    }

    [Test]
    public async Task Post_OnASealedDay_IsRefused_NothingWritten()
    {
        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPostHandler().Handle(
            new PostCommand<ScheduleCommandResource>(NewResource(Guid.NewGuid(), SealedDay)), CancellationToken.None));

        ex.Message.ShouldBe(SealedMessage);
        await _repository.DidNotReceive().Add(Arg.Any<ScheduleCommand>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_OnAnOpenDay_IsWritten()
    {
        var result = await NewPostHandler().Handle(
            new PostCommand<ScheduleCommandResource>(NewResource(Guid.NewGuid(), OpenDay)), CancellationToken.None);

        result.ShouldNotBeNull();
        await _dayLockService.Received(1).EnsureNotLockedAsync(OpenDay, _clientId, null, Arg.Any<CancellationToken>());
        await _repository.Received(1).Add(Arg.Any<ScheduleCommand>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Post_OfAScenarioCommand_HandsTheScenarioTokenToTheDayLock()
    {
        var token = Guid.NewGuid();
        var resource = NewResource(Guid.NewGuid(), SealedDay);
        resource.AnalyseToken = token;

        var result = await NewPostHandler().Handle(
            new PostCommand<ScheduleCommandResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _dayLockService.Received(1).EnsureNotLockedAsync(SealedDay, _clientId, token, Arg.Any<CancellationToken>());
        await _repository.Received(1).Add(Arg.Any<ScheduleCommand>());
    }

    [Test]
    public async Task Put_OnASealedDay_IsRefused_NothingWritten()
    {
        var stored = NewCommand(SealedDay);
        _repository.GetNoTracking(stored.Id).Returns(stored);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, SealedDay)), CancellationToken.None));

        await AssertNothingPutAsync();
    }

    [Test]
    public async Task Put_MovingOutOfASealedDay_IsRefused_NothingWritten()
    {
        var stored = NewCommand(SealedDay);
        _repository.GetNoTracking(stored.Id).Returns(stored);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, OpenDay)), CancellationToken.None));

        await AssertNothingPutAsync();
    }

    [Test]
    public async Task Put_MovingIntoASealedDay_IsRefused_NothingWritten()
    {
        var stored = NewCommand(OpenDay);
        _repository.GetNoTracking(stored.Id).Returns(stored);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, SealedDay)), CancellationToken.None));

        await AssertNothingPutAsync();
    }

    [Test]
    public async Task Put_ReassigningToAnotherClient_ChecksTheStoredOwnerToo()
    {
        var stored = NewCommand(OpenDay);
        _repository.GetNoTracking(stored.Id).Returns(stored);
        var newOwnerId = Guid.NewGuid();
        var resource = NewResource(stored.Id, OpenDay);
        resource.ClientId = newOwnerId;

        var result = await NewPutHandler().Handle(
            new PutCommand<ScheduleCommandResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _dayLockService.Received(1).EnsureNotLockedAsync(OpenDay, newOwnerId, null, Arg.Any<CancellationToken>());
        await _dayLockService.Received(1).EnsureNotLockedAsync(OpenDay, _clientId, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_OnAnOpenDay_IsWritten()
    {
        var stored = NewCommand(OpenDay);
        _repository.GetNoTracking(stored.Id).Returns(stored);

        var result = await NewPutHandler().Handle(
            new PutCommand<ScheduleCommandResource>(NewResource(stored.Id, OpenDay)), CancellationToken.None);

        result.ShouldNotBeNull();
        await _repository.Received(1).Put(Arg.Any<ScheduleCommand>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Delete_OnASealedDay_IsRefused_NothingDeleted()
    {
        var stored = NewCommand(SealedDay);
        _repository.Get(stored.Id).Returns(stored);

        await Should.ThrowAsync<InvalidRequestException>(() => NewDeleteHandler().Handle(
            new DeleteCommand<ScheduleCommandResource>(stored.Id), CancellationToken.None));

        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_OnAnOpenDay_IsDeleted()
    {
        var stored = NewCommand(OpenDay);
        _repository.Get(stored.Id).Returns(stored);

        var result = await NewDeleteHandler().Handle(
            new DeleteCommand<ScheduleCommandResource>(stored.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _repository.Received(1).Delete(stored.Id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    private PostCommandHandler NewPostHandler() => new(
        _repository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork, _dayLockService,
        Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler() => new(
        _repository, _rowAccessGuard, new ScheduleMapper(), _unitOfWork, _dayLockService,
        Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler NewDeleteHandler() => new(
        _repository, _rowAccessGuard, new ScheduleMapper(), _unitOfWork, _dayLockService,
        Substitute.For<ILogger<DeleteCommandHandler>>());

    private async Task AssertNothingPutAsync()
    {
        await _repository.DidNotReceive().Put(Arg.Any<ScheduleCommand>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private ScheduleCommand NewCommand(DateOnly day) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _clientId,
        CurrentDate = day,
        CommandKeyword = FreeKeyword
    };

    private ScheduleCommandResource NewResource(Guid id, DateOnly day) => new()
    {
        Id = id,
        ClientId = _clientId,
        CurrentDate = day,
        CommandKeyword = FreeKeyword
    };
}
