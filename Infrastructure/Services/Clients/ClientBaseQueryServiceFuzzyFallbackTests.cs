using Shouldly;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Clients;

[TestFixture]
public class ClientBaseQueryServiceFuzzyFallbackTests
{
    private DataBaseContext _context;
    private IClientFuzzySearchService _fuzzySearchService;
    private ClientBaseQueryService _baseQueryService;
    private Client _employee;
    private Client _customer;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _employee = CreateClient("Bergmann", "Amalia", EntityTypeEnum.Employee);
        _customer = CreateClient("Bergstein", "Amalia", EntityTypeEnum.Customer);
        _context.Client.AddRange(_employee, _customer);
        _context.SaveChanges();

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        groupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));

        var searchService = new ClientSearchService();
        _fuzzySearchService = Substitute.For<IClientFuzzySearchService>();

        _baseQueryService = new ClientBaseQueryService(
            _context,
            groupFilterService,
            new ClientSearchFilterService(searchService),
            searchService,
            _fuzzySearchService);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private static Client CreateClient(string name, string firstName, EntityTypeEnum type)
    {
        return new Client
        {
            Id = Guid.NewGuid(),
            Name = name,
            FirstName = firstName,
            Type = type,
            Gender = GenderEnum.Female,
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        };
    }

    private static ClientBaseFilter CreateFilter(string searchString)
    {
        return new ClientBaseFilter
        {
            StartDate = new DateOnly(2026, 8, 1),
            EndDate = new DateOnly(2026, 8, 31),
            SearchString = searchString
        };
    }

    [Test]
    public async Task BuildBaseQuery_WhenSharpSearchMisses_ShouldFallBackToFuzzyCandidates()
    {
        // "Bergmen" is a typo with no substring match, so the sharp search yields zero rows.
        _fuzzySearchService.SearchAsync("Bergmen Amalia", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<Client> { _employee }));

        // Act
        var query = await _baseQueryService.BuildBaseQuery(CreateFilter("Bergmen Amalia"));
        var clients = await query.ToListAsync();

        // Assert
        clients.Count.ShouldBe(1);
        clients[0].Id.ShouldBe(_employee.Id);
    }

    [Test]
    public async Task BuildBaseQuery_WhenSharpSearchHits_ShouldNotInvokeFuzzy()
    {
        // Act
        var query = await _baseQueryService.BuildBaseQuery(CreateFilter("Bergmann Amalia"));
        var clients = await query.ToListAsync();

        // Assert
        clients.Count.ShouldBe(1);
        await _fuzzySearchService.DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default, default);
    }

    [Test]
    public async Task BuildBaseQuery_WithNumericSearch_ShouldNotInvokeFuzzy()
    {
        // Act
        var query = await _baseQueryService.BuildBaseQuery(CreateFilter("99999"));
        await query.ToListAsync();

        // Assert
        await _fuzzySearchService.DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default, default);
    }

    [Test]
    public async Task BuildBaseQuery_FuzzyCandidatesOutsideBaseScope_ShouldStayExcluded()
    {
        // Arrange: fuzzy ranks a Customer, but the base query excludes customers structurally.
        _fuzzySearchService.SearchAsync("Bergstein Amalia", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<Client> { _customer }));

        // Act: sharp search misses because customers are excluded before searching.
        var query = await _baseQueryService.BuildBaseQuery(CreateFilter("Bergstein Amalia"));
        var clients = await query.ToListAsync();

        // Assert
        clients.ShouldBeEmpty();
    }
}
