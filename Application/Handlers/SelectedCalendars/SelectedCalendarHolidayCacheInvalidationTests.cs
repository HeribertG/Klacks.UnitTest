// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that writing a single selected calendar (country/state or its OfficialOverride) through the
/// SelectedCalendars endpoints drops the cached holiday calculators of the affected calendar selection(s)
/// after the commit, exactly like the CalendarSelection handlers already do.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.SelectedCalendars;
using Klacks.Api.Application.Mappers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SelectedCalendars;

[TestFixture]
public class SelectedCalendarHolidayCacheInvalidationTests
{
    private ISelectedCalendarRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IHolidayCalculatorCache _holidayCache = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISelectedCalendarRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _holidayCache = Substitute.For<IHolidayCalculatorCache>();
    }

    [Test]
    public async Task Post_CommitsThenInvalidatesOwningSelection()
    {
        var selectionId = Guid.NewGuid();
        var handler = new PostCommandHandler(
            _repository, new ScheduleMapper(), _unitOfWork, _holidayCache,
            Substitute.For<ILogger<PostCommandHandler>>());

        await handler.Handle(
            new PostCommand<SelectedCalendarResource>(CreateResource(Guid.NewGuid(), selectionId)),
            CancellationToken.None);

        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _holidayCache.Invalidate(selectionId);
        });
    }

    [Test]
    public async Task Put_MovedToOtherSelection_InvalidatesOldAndNewSelection()
    {
        var id = Guid.NewGuid();
        var oldSelectionId = Guid.NewGuid();
        var newSelectionId = Guid.NewGuid();
        _repository.Get(id).Returns(new SelectedCalendar { Id = id, CalendarSelectionId = oldSelectionId, Country = "US", State = "US" });
        var handler = CreatePutHandler();

        var result = await handler.Handle(
            new PutCommand<SelectedCalendarResource>(CreateResource(id, newSelectionId)),
            CancellationToken.None);

        result.ShouldNotBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
        _holidayCache.Received(1).Invalidate(oldSelectionId);
        _holidayCache.Received(1).Invalidate(newSelectionId);
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _holidayCache.Invalidate(oldSelectionId);
            _holidayCache.Invalidate(newSelectionId);
        });
    }

    [Test]
    public async Task Put_OfficialOverrideChangedInSameSelection_InvalidatesSelectionOnce()
    {
        var id = Guid.NewGuid();
        var selectionId = Guid.NewGuid();
        _repository.Get(id).Returns(new SelectedCalendar { Id = id, CalendarSelectionId = selectionId, Country = "US", State = "US" });
        var handler = CreatePutHandler();

        await handler.Handle(
            new PutCommand<SelectedCalendarResource>(CreateResource(id, selectionId)),
            CancellationToken.None);

        _holidayCache.Received(1).Invalidate(selectionId);
        _holidayCache.ReceivedWithAnyArgs(1).Invalidate(default);
    }

    [Test]
    public async Task Put_UnknownSelectedCalendar_DoesNotInvalidate()
    {
        var id = Guid.NewGuid();
        _repository.Get(id).Returns((SelectedCalendar?)null);
        var handler = CreatePutHandler();

        var result = await handler.Handle(
            new PutCommand<SelectedCalendarResource>(CreateResource(id, Guid.NewGuid())),
            CancellationToken.None);

        result.ShouldBeNull();
        _holidayCache.DidNotReceiveWithAnyArgs().Invalidate(default);
    }

    [Test]
    public async Task Delete_ExistingSelectedCalendar_CommitsThenInvalidatesOwningSelection()
    {
        var id = Guid.NewGuid();
        var selectionId = Guid.NewGuid();
        _repository.Get(id).Returns(new SelectedCalendar { Id = id, CalendarSelectionId = selectionId, Country = "US", State = "US" });
        var handler = CreateDeleteHandler();

        var result = await handler.Handle(new DeleteCommand<SelectedCalendarResource>(id), CancellationToken.None);

        result.ShouldNotBeNull();
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _holidayCache.Invalidate(selectionId);
        });
    }

    [Test]
    public async Task Delete_UnknownSelectedCalendar_DoesNotInvalidate()
    {
        var id = Guid.NewGuid();
        _repository.Get(id).Returns((SelectedCalendar?)null);
        var handler = CreateDeleteHandler();

        var result = await handler.Handle(new DeleteCommand<SelectedCalendarResource>(id), CancellationToken.None);

        result.ShouldBeNull();
        _holidayCache.DidNotReceiveWithAnyArgs().Invalidate(default);
    }

    private PutCommandHandler CreatePutHandler() => new(
        _repository, new ScheduleMapper(), _unitOfWork, _holidayCache,
        Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler CreateDeleteHandler() => new(
        _repository, new ScheduleMapper(), _unitOfWork, _holidayCache,
        Substitute.For<ILogger<DeleteCommandHandler>>());

    private static SelectedCalendarResource CreateResource(Guid id, Guid selectionId) => new()
    {
        Id = id,
        CalendarSelectionId = selectionId,
        Country = "US",
        State = "US",
        OfficialOverride = false
    };
}
