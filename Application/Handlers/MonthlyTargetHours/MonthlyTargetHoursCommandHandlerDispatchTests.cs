// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the MonthlyTargetHours Post/Put/Delete handlers raise a MonthlyTargetHoursChangedEvent
/// after the commit: create and delete dispatch the affected month once, an update dispatches the old
/// and the new month (once when unchanged), and a failed operation dispatches nothing.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Handlers.MonthlyTargetHours;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Events;
using Klacks.Api.Application.DTOs.Schedules;
using Microsoft.Extensions.Logging;
using MonthlyTargetHoursEntity = Klacks.Api.Domain.Models.Schedules.MonthlyTargetHours;

namespace Klacks.UnitTest.Application.Handlers.MonthlyTargetHours;

[TestFixture]
public class MonthlyTargetHoursCommandHandlerDispatchTests
{
    private IMonthlyTargetHoursRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IMonthlyTargetHoursRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();
    }

    [Test]
    public async Task Post_CommittedRow_DispatchesChangedEventForItsMonth()
    {
        var handler = CreatePostHandler();
        _repository.GetByYearMonth(2026, 2).Returns((MonthlyTargetHoursEntity?)null);

        await handler.Handle(
            new PostCommand<MonthlyTargetHoursResource>(BuildResource(2026, 2, 168m)), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await AssertDispatched(2026, 2);
    }

    [Test]
    public async Task Put_MonthUnchanged_DispatchesOnce()
    {
        var handler = CreatePutHandler();
        var resource = BuildResource(2026, 2, 160m, Guid.NewGuid());
        _repository.GetByYearMonth(2026, 2).Returns((MonthlyTargetHoursEntity?)null);
        _repository.Get(resource.Id).Returns(BuildEntity(resource.Id, 2026, 2, 168m));
        _repository.Put(Arg.Any<MonthlyTargetHoursEntity>())
            .Returns(callInfo => callInfo.Arg<MonthlyTargetHoursEntity>());

        await handler.Handle(new PutCommand<MonthlyTargetHoursResource>(resource), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
        await AssertDispatched(2026, 2);
    }

    [Test]
    public async Task Put_RowMovedToAnotherMonth_DispatchesOldAndNewMonth()
    {
        var handler = CreatePutHandler();
        var resource = BuildResource(2026, 3, 176m, Guid.NewGuid());
        _repository.GetByYearMonth(2026, 3).Returns((MonthlyTargetHoursEntity?)null);
        _repository.Get(resource.Id).Returns(BuildEntity(resource.Id, 2026, 2, 168m));
        _repository.Put(Arg.Any<MonthlyTargetHoursEntity>())
            .Returns(callInfo => callInfo.Arg<MonthlyTargetHoursEntity>());

        await handler.Handle(new PutCommand<MonthlyTargetHoursResource>(resource), CancellationToken.None);

        await _eventDispatcher.Received(2).DispatchAsync(
            Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
        await AssertDispatched(2026, 2);
        await AssertDispatched(2026, 3);
    }

    [Test]
    public async Task Put_RowNotFound_DispatchesNothing()
    {
        var handler = CreatePutHandler();
        var resource = BuildResource(2026, 2, 168m, Guid.NewGuid());
        _repository.GetByYearMonth(2026, 2).Returns((MonthlyTargetHoursEntity?)null);
        _repository.Get(resource.Id).Returns((MonthlyTargetHoursEntity?)null);
        _repository.Put(Arg.Any<MonthlyTargetHoursEntity>()).Returns((MonthlyTargetHoursEntity?)null);

        await handler.Handle(new PutCommand<MonthlyTargetHoursResource>(resource), CancellationToken.None);

        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ExistingRow_DispatchesChangedEventForItsMonth()
    {
        var handler = CreateDeleteHandler();
        var id = Guid.NewGuid();
        _repository.Get(id).Returns(BuildEntity(id, 2026, 2, 168m));
        _repository.Delete(id).Returns(BuildEntity(id, 2026, 2, 168m));

        await handler.Handle(new DeleteCommand<MonthlyTargetHoursResource>(id), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await AssertDispatched(2026, 2);
    }

    [Test]
    public async Task Delete_MissingRow_DispatchesNothing()
    {
        var handler = CreateDeleteHandler();
        var id = Guid.NewGuid();
        _repository.Get(id).Returns((MonthlyTargetHoursEntity?)null);

        await handler.Handle(new DeleteCommand<MonthlyTargetHoursResource>(id), CancellationToken.None);

        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertDispatched(int year, int month)
    {
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e =>
                e is MonthlyTargetHoursChangedEvent &&
                ((MonthlyTargetHoursChangedEvent)e).Year == year &&
                ((MonthlyTargetHoursChangedEvent)e).Month == month),
            Arg.Any<CancellationToken>());
    }

    private PostCommandHandler CreatePostHandler() => new(
        _repository, _mapper, _unitOfWork, _eventDispatcher, Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler CreatePutHandler() => new(
        _repository, _mapper, _unitOfWork, _eventDispatcher, Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler CreateDeleteHandler() => new(
        _repository, _mapper, _unitOfWork, _eventDispatcher, Substitute.For<ILogger<DeleteCommandHandler>>());

    private static MonthlyTargetHoursResource BuildResource(int year, int month, decimal hours, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Year = year,
        Month = month,
        Hours = hours,
    };

    private static MonthlyTargetHoursEntity BuildEntity(Guid id, int year, int month, decimal hours) => new()
    {
        Id = id,
        Year = year,
        Month = month,
        Hours = hours,
    };
}
