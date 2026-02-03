using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Handlers.BreakPlaceholders;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Services.Common;
using NSubstitute;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Presentation.DTOs.Filter;
using Klacks.Api.Presentation.DTOs.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.FakeData;

namespace Klacks.UnitTest.Repository;

[TestFixture]
internal class BreakPlaceholderTests
{
    private IHttpContextAccessor _httpContextAccessor = null!;
    private DataBaseContext dbContext = null!;
    private ScheduleMapper _scheduleMapper = null!;
    private FilterMapper _filterMapper = null!;
    private ClientMapper _clientMapper = null!;
    private IMediator _mediator = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groupClient = null!;
    private IGroupVisibilityService _groupVisibility = null!;

    [Test]
    public async Task GetClientList_Ok()
    {
        // Arrange
        var clients = Clients.GenerateClients(500, 2023, true);
        var absence = Clients.GenerateAbsences(20);
        var breakPlaceholders = Clients.GenerateBreakPlaceholders(clients, absence, 2023, 200).Where(b => b.From.Year == 2023).ToList();
        var filter = Clients.GenerateBreakFilter(absence, 2023);

        var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();

        DataSeed(clients, absence, breakPlaceholders);

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        var searchFilterService = Substitute.For<IClientSearchFilterService>();

        groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        searchFilterService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);

        var breakPlaceholderRepository = new ClientBreakPlaceholderRepository(dbContext, groupFilterService, searchFilterService);
        var query = new Klacks.Api.Application.Queries.BreakPlaceholders.ListQuery(filter);
        var logger = Substitute.For<ILogger<GetListQueryHandler>>();
        var handler = new GetListQueryHandler(breakPlaceholderRepository, _scheduleMapper, _filterMapper, _clientMapper, logger);

        // Act
        var (result, totalCount) = await handler.Handle(query, default);

        // Assert
        result.Should().NotBeNull();
        result.Count().Should().Be(500);
        totalCount.Should().Be(500);

        var tmpBreakPlaceholders = new List<BreakPlaceholder>();

        foreach (var c in result)
        {
            if (c.BreakPlaceholders.Any())
            {
                tmpBreakPlaceholders.AddRange(c.BreakPlaceholders);
            }
        }

        tmpBreakPlaceholders.Should().NotBeNull();
        tmpBreakPlaceholders.Count().Should().Be(200);
    }

    [Test]
    public async Task GetClientList_WithVirtualScrolling_ReturnsFirstChunk()
    {
        // Arrange
        var clients = Clients.GenerateClients(500, 2023, true);
        var absence = Clients.GenerateAbsences(20);
        var breakPlaceholders = Clients.GenerateBreakPlaceholders(clients, absence, 2023, 200).Where(b => b.From.Year == 2023).ToList();
        var filter = Clients.GenerateBreakFilter(absence, 2023);
        filter.StartRow = 0;
        filter.RowCount = 100;

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);
        dbContext.Database.EnsureCreated();
        DataSeed(clients, absence, breakPlaceholders);

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        var searchFilterService = Substitute.For<IClientSearchFilterService>();

        groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        searchFilterService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);

        var breakPlaceholderRepository = new ClientBreakPlaceholderRepository(dbContext, groupFilterService, searchFilterService);
        var query = new Klacks.Api.Application.Queries.BreakPlaceholders.ListQuery(filter);
        var logger = Substitute.For<ILogger<GetListQueryHandler>>();
        var handler = new GetListQueryHandler(breakPlaceholderRepository, _scheduleMapper, _filterMapper, _clientMapper, logger);

        // Act
        var (result, totalCount) = await handler.Handle(query, default);

        // Assert
        result.Should().NotBeNull();
        result.Count().Should().Be(100);
        totalCount.Should().Be(500);
    }

    [Test]
    public async Task GetClientList_WithVirtualScrolling_ReturnsSecondChunk()
    {
        // Arrange
        var clients = Clients.GenerateClients(500, 2023, true);
        var absence = Clients.GenerateAbsences(20);
        var breakPlaceholders = Clients.GenerateBreakPlaceholders(clients, absence, 2023, 200).Where(b => b.From.Year == 2023).ToList();
        var filter = Clients.GenerateBreakFilter(absence, 2023);
        filter.StartRow = 100;
        filter.RowCount = 50;

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);
        dbContext.Database.EnsureCreated();
        DataSeed(clients, absence, breakPlaceholders);

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        var searchFilterService = Substitute.For<IClientSearchFilterService>();

        groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        searchFilterService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);

        var breakPlaceholderRepository = new ClientBreakPlaceholderRepository(dbContext, groupFilterService, searchFilterService);
        var query = new Klacks.Api.Application.Queries.BreakPlaceholders.ListQuery(filter);
        var logger = Substitute.For<ILogger<GetListQueryHandler>>();
        var handler = new GetListQueryHandler(breakPlaceholderRepository, _scheduleMapper, _filterMapper, _clientMapper, logger);

        // Act
        var (result, totalCount) = await handler.Handle(query, default);

        // Assert
        result.Should().NotBeNull();
        result.Count().Should().Be(50);
        totalCount.Should().Be(500);
    }

    [Test]
    public async Task GetClientList_WithVirtualScrolling_Performance_5000Clients()
    {
        // Arrange
        var clients = Clients.GenerateClients(5000, 2023, true);
        var absence = Clients.GenerateAbsences(20);
        var breakPlaceholders = Clients.GenerateBreakPlaceholders(clients, absence, 2023, 2000).Where(b => b.From.Year == 2023).ToList();
        var filter = Clients.GenerateBreakFilter(absence, 2023);
        filter.StartRow = 0;
        filter.RowCount = 100;

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);
        dbContext.Database.EnsureCreated();
        DataSeed(clients, absence, breakPlaceholders);

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        var searchFilterService = Substitute.For<IClientSearchFilterService>();

        groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        searchFilterService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);

        var breakPlaceholderRepository = new ClientBreakPlaceholderRepository(dbContext, groupFilterService, searchFilterService);
        var query = new Klacks.Api.Application.Queries.BreakPlaceholders.ListQuery(filter);
        var logger = Substitute.For<ILogger<GetListQueryHandler>>();
        var handler = new GetListQueryHandler(breakPlaceholderRepository, _scheduleMapper, _filterMapper, _clientMapper, logger);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var (result, totalCount) = await handler.Handle(query, default);

        // Assert
        stopwatch.Stop();
        result.Should().NotBeNull();
        result.Count().Should().Be(100);
        totalCount.Should().Be(5000);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [SetUp]
    public void Setup()
    {
        _scheduleMapper = new ScheduleMapper();
        _filterMapper = new FilterMapper();
        _clientMapper = new ClientMapper();
        _mediator = Substitute.For<IMediator>();
        _groupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _groupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
                   .Returns(Task.FromResult(new List<Guid>()));
        _groupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
                   .Returns(Task.FromResult(new List<Guid>()));

        _groupVisibility = Substitute.For<IGroupVisibilityService>();
        _groupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _groupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
    }

    [TearDown]
    public void TearDown()
    {
        dbContext.Database.EnsureDeleted();
        dbContext.Dispose();
    }

    private void DataSeed(List<Client> clients, List<Absence> absences, List<BreakPlaceholder> breakPlaceholders)
    {
        var addresses = new List<Address>();
        var memberships = new List<Membership>();
        var communications = new List<Communication>();

        foreach (var item in clients!)
        {
            if (item.Addresses.Any())
            {
                foreach (var address in item.Addresses)
                {
                    addresses.Add(address);
                }
            }

            if (item.Communications.Any())
            {
                foreach (var communication in communications)
                {
                    communications.Add(communication);
                }
            }

            if (item.Membership != null)
            {
                memberships.Add(item.Membership);
            }

            item.Addresses.Clear();
            item.Communications.Clear();
            item.Membership = null;
        }

        dbContext.Client.AddRange(clients);
        dbContext.Address.AddRange(addresses);
        dbContext.Membership.AddRange(memberships);
        dbContext.Communication.AddRange(communications);
        dbContext.Absence.AddRange(absences);
        dbContext.BreakPlaceholder.AddRange(breakPlaceholders);

        dbContext.SaveChanges();
    }
}