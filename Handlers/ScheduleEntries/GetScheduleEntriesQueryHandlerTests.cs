// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests für den GetScheduleEntriesQueryHandler: SearchString-Durchreichung an WorkFilter.
/// </summary>
/// <param name="_workRepository">Mock für Work-Repository mit WorkList-Methode</param>
using FluentAssertions;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Application.Handlers.ScheduleEntries;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ScheduleEntries;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Microsoft.Extensions.Logging;
using NSubstitute;
using DomainWorkFilter = Klacks.Api.Domain.Models.Filters.WorkFilter;

namespace Klacks.UnitTest.Handlers.ScheduleEntries;

[TestFixture]
public class GetScheduleEntriesQueryHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private GetScheduleEntriesQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        var scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        var groupFilterService = Substitute.For<IShiftGroupFilterService>();
        var clientAvailabilityScheduleService = Substitute.For<IClientAvailabilityScheduleService>();
        var scheduleMapper = new ScheduleMapper();
        var logger = Substitute.For<ILogger<GetScheduleEntriesQueryHandler>>();

        _handler = new GetScheduleEntriesQueryHandler(
            scheduleEntriesService,
            groupFilterService,
            _workRepository,
            scheduleMapper,
            clientAvailabilityScheduleService,
            logger);
    }

    [Test]
    public async Task Handle_SearchStringPassedToWorkFilter()
    {
        // Arrange
        DomainWorkFilter? capturedFilter = null;
        _workRepository.WorkList(Arg.Do<DomainWorkFilter>(f => capturedFilter = f))
            .Returns<(List<Client> Clients, int TotalCount)>(_ => throw new OperationCanceledException());

        var filter = new WorkScheduleFilter
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            SearchString = "test",
            OrderBy = "name",
            SortOrder = "asc",
            ShowEmployees = true,
            ShowExtern = true,
            StartRow = 0,
            RowCount = 200,
            PaymentInterval = 2
        };
        var query = new GetScheduleEntriesQuery(filter);

        // Act
        try
        {
            await _handler.Handle(query, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }

        // Assert
        capturedFilter.Should().NotBeNull();
        capturedFilter!.SearchString.Should().Be("test");
    }
}
