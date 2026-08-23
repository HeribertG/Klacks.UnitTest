// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetClientAvailabilityTotalsQueryHandler: delegation to the repository aggregation.
/// </summary>
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.ClientAvailabilities;

[TestFixture]
public class GetClientAvailabilityTotalsQueryHandlerTests
{
    private IClientAvailabilityRepository _repository = null!;
    private GetClientAvailabilityTotalsQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IClientAvailabilityRepository>();
        _handler = new GetClientAvailabilityTotalsQueryHandler(
            _repository,
            Substitute.For<ILogger<GetClientAvailabilityTotalsQueryHandler>>());
    }

    [Test]
    public async Task Handle_ReturnsRepositoryResultUnchanged()
    {
        var clientId = Guid.NewGuid();
        var expected = new List<ClientAvailabilityTotalResource>
        {
            new() { ClientId = clientId, TotalHours = 12, DaysWithAvailability = 3 }
        };
        _repository.GetTotalsByClientsAndDateRange(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(expected);

        var result = await _handler.Handle(
            new GetClientAvailabilityTotalsQuery(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new List<Guid> { clientId }),
            CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Test]
    public async Task Handle_PassesClientIdsAndDateRangeToRepository()
    {
        _repository.GetTotalsByClientsAndDateRange(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new List<ClientAvailabilityTotalResource>());

        var clientIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var startDate = new DateOnly(2026, 5, 1);
        var endDate = new DateOnly(2026, 5, 31);

        await _handler.Handle(new GetClientAvailabilityTotalsQuery(startDate, endDate, clientIds), CancellationToken.None);

        await _repository.Received(1).GetTotalsByClientsAndDateRange(clientIds, startDate, endDate);
    }

    [Test]
    public async Task Handle_EmptyClientIds_ReturnsEmptyResult()
    {
        _repository.GetTotalsByClientsAndDateRange(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new List<ClientAvailabilityTotalResource>());

        var result = await _handler.Handle(
            new GetClientAvailabilityTotalsQuery(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new List<Guid>()),
            CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
