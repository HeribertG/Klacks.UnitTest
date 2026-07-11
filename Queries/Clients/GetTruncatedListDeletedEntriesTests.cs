using System.Security.Claims;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Queries.Clients;

[TestFixture]
internal class GetTruncatedListDeletedEntriesTests
{
    private const string ActiveClientName = "Active Client";
    private const string DeletedClientName = "Deleted Client";

    private DataBaseContext _dbContext = null!;
    private ClientMapper _clientMapper = null!;
    private FilterMapper _filterMapper = null!;
    private IClientGroupFilterService _groupFilterService = null!;
    private IClientRepository _clientRepository = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();
        SeedClients();

        _clientMapper = new ClientMapper();
        _filterMapper = new FilterMapper();

        _groupFilterService = Substitute.For<IClientGroupFilterService>();
        _groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));

        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.LastChangeMetaData()
            .Returns(Task.FromResult(new LastChangeMetaData
            {
                Author = "TestUser",
                LastChangesDate = DateTime.UtcNow
            }));
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [Test]
    public async Task Handle_ShowDeleteEntriesAsAdmin_ReturnsDeletedClients()
    {
        // Arrange
        var handler = CreateHandler(isAdmin: true);
        var query = new GetTruncatedListQuery(CreateFilterRequestingDeletedEntries());

        // Act
        var result = await handler.Handle(query, default);

        // Assert
        result.ShouldNotBeNull();
        result.Clients!.ShouldContain(c => c.Name == DeletedClientName,
            "Admins must see soft-deleted clients when the deleted-entries filter is set");
        result.Clients!.ShouldNotContain(c => c.Name == ActiveClientName,
            "The deleted-entries view shows only soft-deleted clients");
    }

    [Test]
    public async Task Handle_ShowDeleteEntriesAsNonAdmin_IgnoresFlagAndReturnsOnlyActiveClients()
    {
        // Arrange
        var handler = CreateHandler(isAdmin: false);
        var query = new GetTruncatedListQuery(CreateFilterRequestingDeletedEntries());

        // Act
        var result = await handler.Handle(query, default);

        // Assert
        result.ShouldNotBeNull();
        result.Clients!.ShouldContain(c => c.Name == ActiveClientName,
            "Non-admins get the regular client list when they request deleted entries");
        result.Clients!.ShouldNotContain(c => c.Name == DeletedClientName,
            "Non-admins must never see soft-deleted clients");
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

    private GetTruncatedListQueryHandler CreateHandler(bool isAdmin)
    {
        var filterRepository = new ClientFilterRepository(
            _dbContext,
            _groupFilterService,
            new Klacks.Api.Domain.Services.Clients.ClientFilterService(),
            new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService(),
            new Klacks.Api.Domain.Services.Clients.ClientSearchService(),
            new Klacks.Api.Domain.Services.Clients.ClientSortingService(),
            Substitute.For<Klacks.Api.Application.Interfaces.IClientFuzzySearchService>());

        var claims = isAdmin
            ? new[] { new Claim(ClaimTypes.Role, Roles.Admin) }
            : Array.Empty<Claim>();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(httpContext);

        var logger = Substitute.For<ILogger<GetTruncatedListQueryHandler>>();

        return new GetTruncatedListQueryHandler(
            filterRepository,
            _clientRepository,
            _clientMapper,
            _filterMapper,
            httpContextAccessor,
            logger);
    }

    private static FilterResource CreateFilterRequestingDeletedEntries()
    {
        var filter = FakeData.Clients.Filter();
        filter.SearchString = string.Empty;
        filter.SearchOnlyByName = true;
        filter.ShowDeleteEntries = true;
        filter.Employee = true;
        filter.ExternEmp = true;
        filter.Customer = true;
        return filter;
    }
}
