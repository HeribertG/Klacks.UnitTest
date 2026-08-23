// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetClientAvailabilityRangesQueryHandler: pass-through mapping from the SQL-function-backed schedule service.
/// </summary>
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.ClientAvailabilities;

[TestFixture]
public class GetClientAvailabilityRangesQueryHandlerTests
{
    private IClientAvailabilityScheduleService _scheduleService = null!;
    private GetClientAvailabilityRangesQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _scheduleService = Substitute.For<IClientAvailabilityScheduleService>();
        _handler = new GetClientAvailabilityRangesQueryHandler(
            _scheduleService,
            Substitute.For<ILogger<GetClientAvailabilityRangesQueryHandler>>());
    }

    [Test]
    public async Task Handle_MapsEntryFieldsToResource()
    {
        var clientId = Guid.NewGuid();
        var entry = new ClientAvailabilityScheduleEntry
        {
            ClientId = clientId,
            AvailabilityDate = new DateTime(2026, 3, 10),
            AvailabilityRanges = "08:00-12:00,14:00-18:00"
        };
        _scheduleService.GetClientAvailabilityQuery(default, default, default)
            .ReturnsForAnyArgs(new TestAsyncEnumerable<ClientAvailabilityScheduleEntry>(new[] { entry }));

        var result = await _handler.Handle(
            new GetClientAvailabilityRangesQuery(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new List<Guid> { clientId }),
            CancellationToken.None);

        result.ShouldHaveSingleItem();
        result[0].ClientId.ShouldBe(clientId);
        result[0].Date.ShouldBe(new DateOnly(2026, 3, 10));
        result[0].Ranges.ShouldBe("08:00-12:00,14:00-18:00");
    }

    [Test]
    public async Task Handle_MultipleEntries_MapsAllInOrder()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var entries = new[]
        {
            new ClientAvailabilityScheduleEntry { ClientId = clientA, AvailabilityDate = new DateTime(2026, 3, 1), AvailabilityRanges = "09:00-11:00" },
            new ClientAvailabilityScheduleEntry { ClientId = clientB, AvailabilityDate = new DateTime(2026, 3, 2), AvailabilityRanges = "13:00-17:00" },
        };
        _scheduleService.GetClientAvailabilityQuery(default, default, default)
            .ReturnsForAnyArgs(new TestAsyncEnumerable<ClientAvailabilityScheduleEntry>(entries));

        var result = await _handler.Handle(
            new GetClientAvailabilityRangesQuery(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new List<Guid> { clientA, clientB }),
            CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].ClientId.ShouldBe(clientA);
        result[1].ClientId.ShouldBe(clientB);
    }

    [Test]
    public async Task Handle_EmptyClientIds_ReturnsEmptyResult()
    {
        _scheduleService.GetClientAvailabilityQuery(default, default, default)
            .ReturnsForAnyArgs(new TestAsyncEnumerable<ClientAvailabilityScheduleEntry>(Enumerable.Empty<ClientAvailabilityScheduleEntry>()));

        var result = await _handler.Handle(
            new GetClientAvailabilityRangesQuery(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new List<Guid>()),
            CancellationToken.None);

        result.ShouldBeEmpty();
        _scheduleService.Received(1).GetClientAvailabilityQuery(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), Arg.Is<List<Guid>>(l => l.Count == 0));
    }

    [Test]
    public async Task Handle_PassesStartEndDateAndClientIdsToService()
    {
        var clientId = Guid.NewGuid();
        _scheduleService.GetClientAvailabilityQuery(default, default, default)
            .ReturnsForAnyArgs(new TestAsyncEnumerable<ClientAvailabilityScheduleEntry>(Enumerable.Empty<ClientAvailabilityScheduleEntry>()));

        var startDate = new DateOnly(2026, 4, 1);
        var endDate = new DateOnly(2026, 4, 30);
        await _handler.Handle(new GetClientAvailabilityRangesQuery(startDate, endDate, new List<Guid> { clientId }), CancellationToken.None);

        _scheduleService.Received(1).GetClientAvailabilityQuery(
            startDate, endDate, Arg.Is<List<Guid>>(l => l.Single() == clientId));
    }
}
