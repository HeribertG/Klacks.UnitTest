// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ClientAvailabilityRepository.GetTotalsByClientsAndDateRange: hour/day aggregation and soft-delete filtering.
/// </summary>
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientAvailabilityRepositoryTests
{
    private DataBaseContext _dbContext = null!;
    private ClientAvailabilityRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _dbContext = new DataBaseContext(options, httpContextAccessor);
        _repository = new ClientAvailabilityRepository(_dbContext, Substitute.For<ILogger<ClientAvailability>>());
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task GetTotalsByClientsAndDateRange_SumsHoursAndCountsDistinctDays()
    {
        var clientId = Guid.NewGuid();
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 8, true);
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 9, true);
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 10, true);
        AddAvailability(clientId, new DateOnly(2026, 3, 2), 14, true);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        result.ShouldHaveSingleItem();
        result[0].ClientId.ShouldBe(clientId);
        result[0].TotalHours.ShouldBe(4);
        result[0].DaysWithAvailability.ShouldBe(2);
    }

    [Test]
    public async Task GetTotalsByClientsAndDateRange_ExcludesUnavailableHours()
    {
        var clientId = Guid.NewGuid();
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 8, true);
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 9, false);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        result.ShouldHaveSingleItem();
        result[0].TotalHours.ShouldBe(1);
        result[0].DaysWithAvailability.ShouldBe(1);
    }

    [Test]
    public async Task GetTotalsByClientsAndDateRange_ExcludesSoftDeletedRows()
    {
        var clientId = Guid.NewGuid();
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 8, true);
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 9, true, isDeleted: true);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        result.ShouldHaveSingleItem();
        result[0].TotalHours.ShouldBe(1);
        result[0].DaysWithAvailability.ShouldBe(1);
    }

    [Test]
    public async Task GetTotalsByClientsAndDateRange_ExcludesRowsOutsideDateRange()
    {
        var clientId = Guid.NewGuid();
        AddAvailability(clientId, new DateOnly(2026, 2, 28), 8, true);
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 8, true);
        AddAvailability(clientId, new DateOnly(2026, 3, 31), 8, true);
        AddAvailability(clientId, new DateOnly(2026, 4, 1), 8, true);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        result.ShouldHaveSingleItem();
        result[0].TotalHours.ShouldBe(2);
        result[0].DaysWithAvailability.ShouldBe(2);
    }

    [Test]
    public async Task GetTotalsByClientsAndDateRange_ExcludesClientsNotInFilter()
    {
        var clientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        AddAvailability(clientId, new DateOnly(2026, 3, 1), 8, true);
        AddAvailability(otherClientId, new DateOnly(2026, 3, 1), 8, true);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        result.ShouldHaveSingleItem();
        result[0].ClientId.ShouldBe(clientId);
    }

    [Test]
    public async Task GetTotalsByClientsAndDateRange_ClientWithNoAvailableRows_IsAbsentFromResult()
    {
        var clientId = Guid.NewGuid();

        var result = await _repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetTotalsByClientsAndDateRange_MultipleClients_AggregatesSeparately()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        AddAvailability(clientA, new DateOnly(2026, 3, 1), 8, true);
        AddAvailability(clientA, new DateOnly(2026, 3, 2), 9, true);
        AddAvailability(clientB, new DateOnly(2026, 3, 1), 10, true);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientA, clientB }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        result.Count.ShouldBe(2);
        var clientAResult = result.Single(r => r.ClientId == clientA);
        clientAResult.TotalHours.ShouldBe(2);
        clientAResult.DaysWithAvailability.ShouldBe(2);
        var clientBResult = result.Single(r => r.ClientId == clientB);
        clientBResult.TotalHours.ShouldBe(1);
        clientBResult.DaysWithAvailability.ShouldBe(1);
    }

    private void AddAvailability(Guid clientId, DateOnly date, int hour, bool isAvailable, bool isDeleted = false)
    {
        _dbContext.ClientAvailability.Add(new ClientAvailability
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Date = date,
            Hour = hour,
            IsAvailable = isAvailable,
            IsDeleted = isDeleted
        });
    }
}
