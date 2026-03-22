// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the ListClientsQueryHandler: paging, mapping, and base query delegation.
/// </summary>
/// <param name="_baseQueryService">Mock for the central ClientBaseQueryService</param>
using FluentAssertions;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.ClientAvailabilities;

[TestFixture]
public class ListClientsQueryHandlerTests
{
    private DataBaseContext _context = null!;
    private IClientBaseQueryService _baseQueryService = null!;
    private ListClientsQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        _baseQueryService = Substitute.For<IClientBaseQueryService>();

        var logger = Substitute.For<ILogger<ListClientsQueryHandler>>();
        _handler = new ListClientsQueryHandler(_baseQueryService, logger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task Handle_DelegatesToBaseQueryService()
    {
        // Arrange
        SeedClients(3);
        SetupBaseQueryToReturnAllClients();

        var filter = CreateDefaultFilter();
        filter.SearchString = "TestSearch";
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        await _handler.Handle(query, CancellationToken.None);

        // Assert
        await _baseQueryService.Received(1).BuildBaseQuery(
            Arg.Is<ClientBaseFilter>(f => f.SearchString == "TestSearch"));
    }

    [Test]
    public async Task Handle_WithPaging_ReturnsCorrectSubset()
    {
        // Arrange
        SeedClients(5);
        SetupBaseQueryToReturnAllClients();

        var filter = CreateDefaultFilter();
        filter.StartRow = 2;
        filter.RowCount = 2;
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.TotalCount.Should().Be(5);
        result.Clients.Should().HaveCount(2);
    }

    [Test]
    public async Task Handle_MapsClientResourceCorrectly()
    {
        // Arrange
        SeedClients(1);
        SetupBaseQueryToReturnAllClients();

        var filter = CreateDefaultFilter();
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Clients.Should().HaveCount(1);
        result.Clients[0].Name.Should().Be("Client0");
        result.Clients[0].FirstName.Should().Be("First0");
    }

    [Test]
    public async Task Handle_PassesFilterParametersToBaseQuery()
    {
        // Arrange
        SeedClients(1);
        SetupBaseQueryToReturnAllClients();

        var groupId = Guid.NewGuid();
        var filter = CreateDefaultFilter();
        filter.SelectedGroup = groupId;
        filter.ShowEmployees = true;
        filter.ShowExtern = false;
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        await _handler.Handle(query, CancellationToken.None);

        // Assert
        await _baseQueryService.Received(1).BuildBaseQuery(
            Arg.Is<ClientBaseFilter>(f =>
                f.SelectedGroup == groupId &&
                f.ShowEmployees == true &&
                f.ShowExtern == false));
    }

    private void SetupBaseQueryToReturnAllClients()
    {
        _baseQueryService.BuildBaseQuery(Arg.Any<ClientBaseFilter>())
            .Returns(_ => Task.FromResult(_context.Client
                .Include(c => c.Membership)
                .Where(c => c.Type != EntityTypeEnum.Customer)
                .AsQueryable()));
    }

    private void SeedClients(int count)
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            _context.Client.Add(new Client
            {
                Id = Guid.NewGuid(),
                Name = $"Client{i}",
                FirstName = $"First{i}",
                Type = EntityTypeEnum.Employee,
                Membership = new Membership
                {
                    Id = Guid.NewGuid(),
                    ValidFrom = now.AddYears(-1),
                    ValidUntil = null
                }
            });
        }
        _context.SaveChanges();
    }

    private static ClientAvailabilityClientFilter CreateDefaultFilter()
    {
        return new ClientAvailabilityClientFilter
        {
            SearchString = string.Empty,
            StartDate = new DateOnly(2026, 3, 1),
            EndDate = new DateOnly(2026, 3, 31),
            SelectedGroup = null,
            OrderBy = "name",
            SortOrder = "asc",
            ShowEmployees = true,
            ShowExtern = true,
            StartRow = 0,
            RowCount = 200
        };
    }
}
