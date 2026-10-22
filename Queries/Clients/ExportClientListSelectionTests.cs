// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes the selection and the row shape of the client export over the real ClientFilterRepository (EF
/// InMemory): a selection without SelectAll keeps only the selected clients, an inverted selection drops them,
/// SelectAll ignores a plain selection, and an empty selection exports every client the filter keeps. The IdNumber
/// order is pinned against PostgreSQL in ClientListReadShapesTests (InMemory does not generate IdNumbers, so every row
/// ties there).
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Domain.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Queries.Clients;

[TestFixture]
internal class ExportClientListSelectionTests
{
    private DataBaseContext _dbContext = null!;
    private ExportClientListQueryHandler _handler = null!;
    private Client _zulu = null!;
    private Client _alpha = null!;
    private Client _mike = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _zulu = await SeedAsync("Zulu", GenderEnum.Male, EntityTypeEnum.Employee);
        _alpha = await SeedAsync("Alpha", GenderEnum.Female, EntityTypeEnum.ExternEmp);
        _mike = await SeedAsync("Mike", GenderEnum.Female, EntityTypeEnum.Employee);

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>(), Arg.Any<bool>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));

        var filterRepository = new ClientFilterRepository(
            _dbContext,
            groupFilterService,
            new Klacks.Api.Domain.Services.Clients.ClientFilterService(),
            new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService(),
            new Klacks.Api.Domain.Services.Clients.ClientSearchService(),
            new Klacks.Api.Domain.Services.Clients.ClientSortingService(),
            Substitute.For<IClientFuzzySearchService>());

        _handler = new ExportClientListQueryHandler(
            filterRepository,
            new FilterMapper(),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<ILogger<ExportClientListQueryHandler>>());
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    [Test]
    public async Task SelectAll_ExportsEveryKeptClient_WithTheExportColumns()
    {
        var result = await ExportAsync(selectAll: true, inverted: false, []);

        result.Select(r => r.Name).ShouldBe(["Zulu", "Alpha", "Mike"], ignoreOrder: true);
        var alpha = result.Single(r => r.Name == "Alpha");
        alpha.Gender.ShouldBe((int)GenderEnum.Female);
        alpha.Type.ShouldBe((int)EntityTypeEnum.ExternEmp);
        alpha.FirstName.ShouldBe("First");
        alpha.Company.ShouldBe(string.Empty);
    }

    [Test]
    public async Task PlainSelection_KeepsOnlyTheSelectedClients()
    {
        var result = await ExportAsync(selectAll: false, inverted: false, [_mike.Id, _zulu.Id]);

        result.Select(r => r.Name).ShouldBe(["Zulu", "Mike"], ignoreOrder: true);
    }

    [Test]
    public async Task InvertedSelection_DropsTheSelectedClients()
    {
        var result = await ExportAsync(selectAll: true, inverted: true, [_zulu.Id]);

        result.Select(r => r.Name).ShouldBe(["Alpha", "Mike"], ignoreOrder: true);
    }

    [Test]
    public async Task SelectAllWithAPlainSelection_IgnoresTheSelection()
    {
        var result = await ExportAsync(selectAll: true, inverted: false, [_zulu.Id]);

        result.Select(r => r.Name).ShouldBe(["Zulu", "Alpha", "Mike"], ignoreOrder: true);
    }

    [Test]
    public async Task EmptySelectionWithoutSelectAll_ExportsEveryKeptClient()
    {
        var result = await ExportAsync(selectAll: false, inverted: false, []);

        result.Select(r => r.Name).ShouldBe(["Zulu", "Alpha", "Mike"], ignoreOrder: true);
    }

    [Test]
    public async Task InvertedEmptySelection_ExportsEveryKeptClient()
    {
        var result = await ExportAsync(selectAll: false, inverted: true, []);

        result.Select(r => r.Name).ShouldBe(["Zulu", "Alpha", "Mike"], ignoreOrder: true);
    }

    private Task<List<ExportClientItemDto>> ExportAsync(bool selectAll, bool inverted, List<Guid> selection)
    {
        var filter = FakeData.Clients.Filter();
        filter.SearchString = string.Empty;
        filter.SearchOnlyByName = true;
        filter.ShowDeleteEntries = false;
        filter.Employee = true;
        filter.ExternEmp = true;
        filter.Customer = true;
        filter.OrderBy = "name";
        filter.SortOrder = "asc";

        var request = new ExportClientRequest
        {
            Filter = filter,
            SelectAll = selectAll,
            InvertedSelection = inverted,
            Selection = selection,
        };
        return _handler.Handle(new ExportClientListQuery(request), CancellationToken.None);
    }

    private async Task<Client> SeedAsync(string name, GenderEnum gender, EntityTypeEnum type)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = name,
            FirstName = "First",
            Gender = gender,
            Type = type,
        };
        _dbContext.Client.Add(client);
        await _dbContext.SaveChangesAsync();
        return client;
    }
}
