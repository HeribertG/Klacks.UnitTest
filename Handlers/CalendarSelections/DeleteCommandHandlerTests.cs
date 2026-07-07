// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.CalendarSelections;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.CalendarSelections;

[TestFixture]
public class DeleteCommandHandlerTests
{
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<DeleteCommandHandler>>();
        _handler = new DeleteCommandHandler(
            _calendarSelectionRepository,
            _settingsRepository,
            new ScheduleMapper(),
            _unitOfWork,
            logger);
    }

    [Test]
    public async Task Handle_SeededSelection_ThrowsAndDoesNotDelete()
    {
        var id = Guid.NewGuid();
        var selection = new CalendarSelection
        {
            Id = id,
            Name = "Kanton Zürich",
            IsSeeded = true
        };
        _calendarSelectionRepository.GetWithSelectedCalendars(id).Returns(selection);

        Func<Task> act = async () => await _handler.Handle(
            new DeleteCommand<CalendarSelectionResource>(id), CancellationToken.None);

        await act.ShouldThrowAsync<InvalidRequestException>();
        await _calendarSelectionRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_UnseededUnusedSelection_Deletes()
    {
        var id = Guid.NewGuid();
        var selection = new CalendarSelection
        {
            Id = id,
            Name = "Custom Selection",
            IsSeeded = false
        };
        _calendarSelectionRepository.GetWithSelectedCalendars(id).Returns(selection);
        _settingsRepository.GetSetting(Arg.Any<string>()).Returns((Settings?)null);
        _calendarSelectionRepository
            .CountActiveGroupsByCalendarSelectionAsync(id, Arg.Any<CancellationToken>())
            .Returns(0);
        _calendarSelectionRepository
            .CountActiveContractsByCalendarSelectionAsync(id, Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await _handler.Handle(
            new DeleteCommand<CalendarSelectionResource>(id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _calendarSelectionRepository.Received(1).Delete(id);
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
