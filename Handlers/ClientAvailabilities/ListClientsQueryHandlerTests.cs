// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests für den ListClientsQueryHandler: Suche, Gruppenfilter, Paging und Typfilter.
/// </summary>
/// <param name="_context">InMemory-DataBaseContext mit Testdaten</param>
/// <param name="_groupFilterService">Mock für Gruppenfilterung</param>
/// <param name="_searchFilterService">Mock für Suchfilterung</param>
using FluentAssertions;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
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
    private IClientGroupFilterService _groupFilterService = null!;
    private IClientSearchFilterService _searchFilterService = null!;
    private ListClientsQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        _groupFilterService = Substitute.For<IClientGroupFilterService>();
        _searchFilterService = Substitute.For<IClientSearchFilterService>();

        _groupFilterService
            .FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<IQueryable<Client>>(1)));

        _searchFilterService
            .ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo => callInfo.ArgAt<IQueryable<Client>>(0));

        var logger = Substitute.For<ILogger<ListClientsQueryHandler>>();
        _handler = new ListClientsQueryHandler(_context, _groupFilterService, _searchFilterService, logger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task Handle_WithSearchString_FiltersClients()
    {
        // Arrange
        SeedClients(1);
        var filter = CreateDefaultFilter();
        filter.SearchString = "TestSearch";
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        await _handler.Handle(query, CancellationToken.None);

        // Assert
        _searchFilterService.Received(1).ApplySearchFilter(
            Arg.Any<IQueryable<Client>>(),
            Arg.Is("TestSearch"),
            Arg.Is(false));
    }

    [Test]
    public async Task Handle_WithGroupFilter_FiltersByGroup()
    {
        // Arrange
        SeedClients(1);
        var groupId = Guid.NewGuid();
        var filter = CreateDefaultFilter();
        filter.SelectedGroup = groupId;
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        await _handler.Handle(query, CancellationToken.None);

        // Assert
        await _groupFilterService.Received(1).FilterClientsByGroupId(
            Arg.Is(groupId),
            Arg.Any<IQueryable<Client>>());
    }

    [Test]
    public async Task Handle_WithPaging_ReturnsCorrectSubset()
    {
        // Arrange
        SeedClients(5);
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
    public async Task Handle_ShowEmployeesOnly_ExcludesExtern()
    {
        // Arrange
        SeedClientsWithTypes();
        var filter = CreateDefaultFilter();
        filter.ShowEmployees = true;
        filter.ShowExtern = false;
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Clients.Should().OnlyContain(c => c.Name.StartsWith("Employee"));
        result.Clients.Should().HaveCount(2);
    }

    [Test]
    public async Task Handle_EmptySearchString_ReturnsAll()
    {
        // Arrange
        SeedClients(3);
        var filter = CreateDefaultFilter();
        filter.SearchString = "";
        var query = new ListClientAvailabilityClientsQuery(filter);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Clients.Should().HaveCount(3);
        result.TotalCount.Should().Be(3);
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

    private void SeedClientsWithTypes()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 2; i++)
        {
            _context.Client.Add(new Client
            {
                Id = Guid.NewGuid(),
                Name = $"Employee{i}",
                FirstName = $"EmpFirst{i}",
                Type = EntityTypeEnum.Employee,
                Membership = new Membership
                {
                    Id = Guid.NewGuid(),
                    ValidFrom = now.AddYears(-1),
                    ValidUntil = null
                }
            });
        }
        for (var i = 0; i < 2; i++)
        {
            _context.Client.Add(new Client
            {
                Id = Guid.NewGuid(),
                Name = $"Extern{i}",
                FirstName = $"ExtFirst{i}",
                Type = EntityTypeEnum.ExternEmp,
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
