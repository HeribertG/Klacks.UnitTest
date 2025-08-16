using FluentAssertions;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace UnitTest.Repository;

[TestFixture]
public class ClientSearchServiceTests
{
    private DataBaseContext _dbContext;
    private ClientSearchService _searchService;
    private IHttpContextAccessor _httpContextAccessor;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;

        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _dbContext = new DataBaseContext(options, _httpContextAccessor);
        _dbContext.Database.EnsureCreated();

        _searchService = new ClientSearchService();

        // Testdaten erstellen
        var now = DateTime.Now;
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Müller",
                FirstName = "Hans",
                Company = "ABC GmbH",
                Addresses = new List<Address>
                {
                    new Address {
                        Street = "Hauptstraße 1",
                        City = "Berlin",
                        ValidFrom = now
                    }
                }
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Schmidt",
                FirstName = "Peter",
                Company = "XYZ AG",
                Addresses = new List<Address>
                {
                    new Address {
                        Street = "Nebenstraße 2",
                        City = "München",
                        ValidFrom = now
                    }
                }
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Schneider",
                FirstName = "Maria",
                Company = "DEF GmbH",
                Addresses = new List<Address>
                {
                    new Address {
                        Street = "Bergstraße 3",
                        City = "Hamburg",
                        ValidFrom = now
                    }
                }
            }
        };

        _dbContext.Client.AddRange(clients);
        _dbContext.SaveChanges();
    }

    [Test]
    public void ApplyStandardSearch_ShouldFilterByNameAndFirstName()
    {
        // Arrange
        var baseQuery = _dbContext.Client
            .Include(c => c.Addresses)
            .AsNoTracking()
            .AsQueryable();

        // Act
        var result = _searchService.ApplyStandardSearch(baseQuery, new string[] { "müller", "hans" }, false);

        // Assert
        result.Should().NotBeNull();
        var clients = result.ToList();
        clients.Should().HaveCount(1);
        clients.First().Name.Should().Be("Müller");
        clients.First().FirstName.Should().Be("Hans");
    }

    [Test]
    public void ApplyStandardSearch_ShouldFilterByCompany()
    {
        // Arrange
        var baseQuery = _dbContext.Client
            .Include(c => c.Addresses)
            .AsNoTracking()
            .AsQueryable();

        // Act
        var result = _searchService.ApplyStandardSearch(baseQuery, new string[] { "xyz", "ag" }, false);

        // Assert
        result.Should().NotBeNull();
        var clients = result.ToList();
        clients.Should().HaveCount(1);
        clients.First().Name.Should().Be("Schmidt");
        clients.First().Company.Should().Be("XYZ AG");
    }

    [Test]
    public void ApplyStandardSearch_ShouldFilterByAddressWhenIncluded()
    {
        // Arrange
        var baseQuery = _dbContext.Client
            .Include(c => c.Addresses)
            .AsNoTracking()
            .AsQueryable();

        // Act
        var result = _searchService.ApplyStandardSearch(baseQuery, new string[] { "berg" }, true);

        // Assert
        result.Should().NotBeNull();
        var clients = result.ToList();
        clients.Should().HaveCount(1);
        clients.First().Name.Should().Be("Schneider");
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }
}