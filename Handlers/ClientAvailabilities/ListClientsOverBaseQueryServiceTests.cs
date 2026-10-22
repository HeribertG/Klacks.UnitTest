// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes ListClientsQueryHandler over the real ClientBaseQueryService (EF InMemory) instead of a stubbed
/// IQueryable: the total counts every client the base filter keeps, the page is cut with StartRow/RowCount after the
/// base sort, and each row carries the group ids of its non-deleted group items (scenario rows included).
/// </summary>

using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Infrastructure.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.ClientAvailabilities;

[TestFixture]
public class ListClientsOverBaseQueryServiceTests
{
    private DataBaseContext _context = null!;
    private ListClientsQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        var searchService = new ClientSearchService();

        var baseQueryService = new ClientBaseQueryService(
            _context,
            groupFilterService,
            new ClientSearchFilterService(searchService),
            searchService,
            Substitute.For<IClientFuzzySearchService>());

        _handler = new ListClientsQueryHandler(baseQueryService, Substitute.For<ILogger<ListClientsQueryHandler>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task CountsAllKeptClients_AndPagesInNameOrder_WithTheirLiveGroupIds()
    {
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var scenarioGroup = Guid.NewGuid();
        var deletedGroup = Guid.NewGuid();
        Seed("Delta", EntityTypeEnum.Employee);
        Seed("Alpha", EntityTypeEnum.Employee);
        var charlie = Seed("Charlie", EntityTypeEnum.ExternEmp);
        var bravo = Seed("Bravo", EntityTypeEnum.Employee);
        Seed("Echo", EntityTypeEnum.Customer);
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = bravo.Id, GroupId = groupA },
            new GroupItem { Id = Guid.NewGuid(), ClientId = bravo.Id, GroupId = groupB },
            new GroupItem { Id = Guid.NewGuid(), ClientId = charlie.Id, GroupId = scenarioGroup, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ClientId = charlie.Id, GroupId = deletedGroup, IsDeleted = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await _handler.Handle(Query(startRow: 1, rowCount: 2), CancellationToken.None);

        response.TotalCount.ShouldBe(4);
        response.Clients.Select(c => c.Name).ShouldBe(["Bravo", "Charlie"]);
        response.Clients[0].GroupIds.ShouldBe([groupA, groupB], ignoreOrder: true);
        response.Clients[1].GroupIds.ShouldBe([scenarioGroup]);
    }

    [Test]
    public async Task StartRowPastTheEnd_KeepsTheTotal_WithAnEmptyPage()
    {
        Seed("Alpha", EntityTypeEnum.Employee);
        Seed("Bravo", EntityTypeEnum.Employee);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await _handler.Handle(Query(startRow: 5, rowCount: 10), CancellationToken.None);

        response.TotalCount.ShouldBe(2);
        response.Clients.ShouldBeEmpty();
    }

    [Test]
    public async Task DescendingNameSort_IsAppliedBeforeThePage()
    {
        Seed("Alpha", EntityTypeEnum.Employee);
        Seed("Bravo", EntityTypeEnum.Employee);
        Seed("Charlie", EntityTypeEnum.Employee);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var query = Query(startRow: 0, rowCount: 2);
        query.Filter.SortOrder = "desc";

        var response = await _handler.Handle(query, CancellationToken.None);

        response.TotalCount.ShouldBe(3);
        response.Clients.Select(c => c.Name).ShouldBe(["Charlie", "Bravo"]);
    }

    private Client Seed(string name, EntityTypeEnum type)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = name,
            FirstName = "First",
            Type = type,
            Gender = GenderEnum.Female,
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };
        _context.Client.Add(client);
        return client;
    }

    private static ListClientAvailabilityClientsQuery Query(int startRow, int rowCount) => new(
        new ClientAvailabilityClientFilter
        {
            StartDate = new DateOnly(2026, 8, 1),
            EndDate = new DateOnly(2026, 8, 31),
            SearchString = string.Empty,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc",
            StartRow = startRow,
            RowCount = rowCount,
        });
}
