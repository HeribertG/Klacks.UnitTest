// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The client export applies the same deleted-entries rule as the client list: only administrators
/// can export soft-deleted clients; for everybody else the ShowDeleteEntries flag is ignored.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Queries.Clients;

[TestFixture]
internal class ExportClientListDeletedEntriesTests
{
    private const string ActiveClientName = "Active Client";
    private const string DeletedClientName = "Deleted Client";

    private DataBaseContext _dbContext = null!;
    private IClientGroupFilterService _groupFilterService = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();
        SeedClients();

        _groupFilterService = Substitute.For<IClientGroupFilterService>();
        _groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>(), Arg.Any<bool>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [Test]
    public async Task Handle_ShowDeleteEntriesAsAdmin_ExportsDeletedClients()
    {
        var handler = CreateHandler(isAdmin: true);

        var result = await handler.Handle(new ExportClientListQuery(CreateRequestForDeletedEntries()), default);

        result.ShouldContain(c => c.Name == DeletedClientName,
            "Admins must be able to export soft-deleted clients when the deleted-entries filter is set");
        result.ShouldNotContain(c => c.Name == ActiveClientName,
            "The deleted-entries view shows only soft-deleted clients");
    }

    [Test]
    public async Task Handle_ShowDeleteEntriesAsNonAdmin_IgnoresFlagAndExportsOnlyActiveClients()
    {
        var handler = CreateHandler(isAdmin: false);

        var result = await handler.Handle(new ExportClientListQuery(CreateRequestForDeletedEntries()), default);

        result.ShouldContain(c => c.Name == ActiveClientName,
            "Non-admins get the regular client export when they request deleted entries");
        result.ShouldNotContain(c => c.Name == DeletedClientName,
            "Non-admins must never export soft-deleted clients");
    }

    [Test]
    public async Task Handle_ShowDeleteEntriesWithoutHttpUser_IgnoresFlag()
    {
        var handler = CreateHandler(httpContext: null);

        var result = await handler.Handle(new ExportClientListQuery(CreateRequestForDeletedEntries()), default);

        result.ShouldNotContain(c => c.Name == DeletedClientName,
            "Without an administrator there is no deleted-entries export");
    }

    private void SeedClients()
    {
        _dbContext.Client.AddRange(
            new Client
            {
                Id = Guid.NewGuid(),
                Name = ActiveClientName,
                Gender = GenderEnum.Male,
                Type = EntityTypeEnum.Employee
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = DeletedClientName,
                Gender = GenderEnum.Male,
                Type = EntityTypeEnum.Employee,
                IsDeleted = true
            });
        _dbContext.SaveChanges();
    }

    private ExportClientListQueryHandler CreateHandler(bool isAdmin)
    {
        var claims = isAdmin
            ? new[] { new Claim(ClaimTypes.Role, Roles.Admin) }
            : Array.Empty<Claim>();
        return CreateHandler(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        });
    }

    private ExportClientListQueryHandler CreateHandler(HttpContext? httpContext)
    {
        var filterRepository = new ClientFilterRepository(
            _dbContext,
            _groupFilterService,
            new Klacks.Api.Domain.Services.Clients.ClientFilterService(),
            new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService(),
            new Klacks.Api.Domain.Services.Clients.ClientSearchService(),
            new Klacks.Api.Domain.Services.Clients.ClientSortingService(),
            Substitute.For<IClientFuzzySearchService>());

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(httpContext);

        return new ExportClientListQueryHandler(
            filterRepository,
            new FilterMapper(),
            httpContextAccessor,
            Substitute.For<ILogger<ExportClientListQueryHandler>>());
    }

    private static ExportClientRequest CreateRequestForDeletedEntries()
    {
        var filter = FakeData.Clients.Filter();
        filter.SearchString = string.Empty;
        filter.SearchOnlyByName = true;
        filter.ShowDeleteEntries = true;
        filter.Employee = true;
        filter.ExternEmp = true;
        filter.Customer = true;
        return new ExportClientRequest { Filter = filter, SelectAll = true };
    }
}
