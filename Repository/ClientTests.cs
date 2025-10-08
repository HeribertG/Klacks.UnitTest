using AutoMapper;
using Klacks.Api.BasicScriptInterpreter;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Services.Common;
using NSubstitute;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Filter;
using Klacks.Api.Presentation.DTOs.Settings;
using Klacks.Api.Presentation.DTOs.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnitTest.FakeData;
using Klacks.Api.Application.AutoMapper;


namespace UnitTest.Repository;

internal class ClientTests
{

    public IHttpContextAccessor _httpContextAccessor = null!;
    public TruncatedClient _truncatedClient = null!;
    public DataBaseContext dbContext = null!;
    private IMapper _mapper = null!;
    private IClientGroupFilterService _clientGroupFilterService = null!;

    [TestCase("ag", "", "", 12)]
    [TestCase("gmbh", "", "", 0)]
    [TestCase("sa", "", "", 2)]
    [TestCase("Dr", "", "", 1)]
    [TestCase("Zentrum", "", "", 3)]
    [TestCase("15205", "", "", 1)] // Id Number
    [TestCase("15215", "", "", 1)] // Id Number
    [TestCase("", "Male", "", 0)]
    [TestCase("", "Female", "", 0)]
    [TestCase("", "Intersexuality", "", 23)]
    [TestCase("", "LegalEntity", "", 0)]
    [TestCase("", "", "SG", 3)]
    [TestCase("", "", "BE", 4)]
    [TestCase("", "", "ZH", 4)]
    public async Task GetTruncatedListQueryHandler_Filter_Ok(string searchString, string gender, string state, int sum)
    {
        //Arrange
        var returns = Clients.TruncatedClient();
        var filter = Clients.Filter();
        filter.SearchString = searchString;
        filter.SearchOnlyByName = false;

        if (!string.IsNullOrEmpty(gender))
        {
            switch (gender)
            {
                case "Male":
                    filter.Male = true;
                    filter.Female = false;
                    filter.LegalEntity = false;
                    filter.Intersexuality = false;
                    break;

                case "Female":
                    filter.Male = false;
                    filter.Female = true;
                    filter.LegalEntity = false;
                    filter.Intersexuality = false;
                    break;

                case "LegalEntity":
                    filter.Male = false;
                    filter.Female = false;
                    filter.LegalEntity = true;
                    filter.Intersexuality = false;
                    break;

                case "Intersexuality":
                    filter.Male = false;
                    filter.Female = false;
                    filter.LegalEntity = false;
                    filter.Intersexuality = true;
                    break;
            }
        }

        if (!string.IsNullOrEmpty(state))
        {
            foreach (var item in filter.FilteredStateToken)
            {
                item.Select = item.State == state;
            }
            foreach (var item in filter.List)
            {
                item.Select = item.State == state;
            }
        }

        var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);

        // Use real domain services for proper filtering behavior in integration tests
        var clientFilterService = new Klacks.Api.Domain.Services.Clients.ClientFilterService();
        var membershipFilterService = new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService(dbContext);
        var searchService = new Klacks.Api.Domain.Services.Clients.ClientSearchService();
        var sortingService = new Klacks.Api.Domain.Services.Clients.ClientSortingService();
        var changeTrackingService = new Klacks.Api.Domain.Services.Clients.ClientChangeTrackingService(dbContext, sortingService);
        var clientValidator = new Klacks.Api.Domain.Services.Clients.ClientValidator();
        var entityManagementService = new Klacks.Api.Domain.Services.Clients.ClientEntityManagementService(clientValidator);
        var workFilterService = new Klacks.Api.Domain.Services.Clients.ClientWorkFilterService();
        var collectionUpdateService = new Klacks.Api.Infrastructure.Services.EntityCollectionUpdateService(dbContext);

        var repository = new ClientRepository(dbContext, new MacroEngine(),
            changeTrackingService, entityManagementService, collectionUpdateService, clientValidator);

        var filterRepository = new ClientFilterRepository(dbContext, _clientGroupFilterService,
            clientFilterService, membershipFilterService, searchService, sortingService);

        var mappedFilter = _mapper.Map<Klacks.Api.Domain.Models.Filters.ClientFilter>(filter);
        var selectedStates = mappedFilter.FilteredStateToken?.Where(x => x.Select).Select(x => x.State).ToList();


        var query = new GetTruncatedListQuery(filter);
        var logger = Substitute.For<ILogger<GetTruncatedListQueryHandler>>();
        var handler = new GetTruncatedListQueryHandler(filterRepository, repository, _mapper, logger);
        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        result.Clients.Should().HaveCount(sum);
    }

    /// <summary>
    /// The mocked TruncatedClient result has 24 entries.
    /// </summary>
    [TestCase(10, 3)]
    [TestCase(15, 2)]
    [TestCase(24, 1)]
    public async Task GetTruncatedListQueryHandler_Pagination_NotOk(int numberOfItemsPerPage, int requiredPage)
    {
        //Arrange
        var returns = Clients.TruncatedClient();
        var filter = Clients.Filter();
        filter.NumberOfItemsPerPage = numberOfItemsPerPage;
        filter.RequiredPage = requiredPage;
        var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);

        // Use real domain services for proper filtering behavior in integration tests
        var clientFilterService = new Klacks.Api.Domain.Services.Clients.ClientFilterService();
        var membershipFilterService = new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService(dbContext);
        var searchService = new Klacks.Api.Domain.Services.Clients.ClientSearchService();
        var sortingService = new Klacks.Api.Domain.Services.Clients.ClientSortingService();
        var changeTrackingService = new Klacks.Api.Domain.Services.Clients.ClientChangeTrackingService(dbContext, sortingService);
        var clientValidator = new Klacks.Api.Domain.Services.Clients.ClientValidator();
        var entityManagementService = new Klacks.Api.Domain.Services.Clients.ClientEntityManagementService(clientValidator);
        var workFilterService = new Klacks.Api.Domain.Services.Clients.ClientWorkFilterService();
        var collectionUpdateService = new Klacks.Api.Infrastructure.Services.EntityCollectionUpdateService(dbContext);

        var repository = new ClientRepository(dbContext, new MacroEngine(),
            changeTrackingService, entityManagementService, collectionUpdateService, clientValidator);

        var filterRepository = new ClientFilterRepository(dbContext, _clientGroupFilterService,
            clientFilterService, membershipFilterService, searchService, sortingService);
        var query = new GetTruncatedListQuery(filter);
        var logger = Substitute.For<ILogger<GetTruncatedListQueryHandler>>();
        var handler = new GetTruncatedListQueryHandler(filterRepository, repository, _mapper, logger);
        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        result.Clients.Should().HaveCount(0);
        result.FirstItemOnPage.Should().Be(-1);
    }

    /// <summary>
    /// The mocked TruncatedClient result has 24 entries.
    /// </summary>
    [TestCase(5, 0, 5)]
    [TestCase(10, 0, 10)]
    [TestCase(15, 0, 15)]
    [TestCase(20, 0, 20)]
    [TestCase(5, 1, 5)]
    [TestCase(10, 1, 10)]
    [TestCase(15, 1, 9)]
    [TestCase(20, 1, 4)]
    public async Task GetTruncatedListQueryHandler_Pagination_Ok(int numberOfItemsPerPage, int requiredPage, int maxItems)
    {
        //Arrange
        var returns = Clients.TruncatedClient();
        var filter = Clients.Filter();
        filter.NumberOfItemsPerPage = numberOfItemsPerPage;
        filter.RequiredPage = requiredPage;
        var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);

        // Use real domain services for proper filtering behavior in integration tests
        var clientFilterService = new Klacks.Api.Domain.Services.Clients.ClientFilterService();
        var membershipFilterService = new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService(dbContext);
        var searchService = new Klacks.Api.Domain.Services.Clients.ClientSearchService();
        var sortingService = new Klacks.Api.Domain.Services.Clients.ClientSortingService();
        var changeTrackingService = new Klacks.Api.Domain.Services.Clients.ClientChangeTrackingService(dbContext, sortingService);
        var clientValidator = new Klacks.Api.Domain.Services.Clients.ClientValidator();
        var entityManagementService = new Klacks.Api.Domain.Services.Clients.ClientEntityManagementService(clientValidator);
        var workFilterService = new Klacks.Api.Domain.Services.Clients.ClientWorkFilterService();
        var collectionUpdateService = new Klacks.Api.Infrastructure.Services.EntityCollectionUpdateService(dbContext);

        var repository = new ClientRepository(dbContext, new MacroEngine(),
            changeTrackingService, entityManagementService, collectionUpdateService, clientValidator);

        var filterRepository = new ClientFilterRepository(dbContext, _clientGroupFilterService,
            clientFilterService, membershipFilterService, searchService, sortingService);
        var query = new GetTruncatedListQuery(filter);
        var logger = Substitute.For<ILogger<GetTruncatedListQueryHandler>>();
        var handler = new GetTruncatedListQueryHandler(filterRepository, repository, _mapper, logger);
        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        result.Clients.Should().HaveCount(maxItems);
        result.MaxItems.Should().Be(returns.Clients!.Count());
        result.CurrentPage.Should().Be(requiredPage);
        result.FirstItemOnPage.Should().Be(numberOfItemsPerPage * (requiredPage));
    }

    // [Test] - Disabled: Test moved to ClientSearchServiceTests
    public async Task FilterBySearchStringStandard_ShouldFilterCorrectly_New()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;

        dbContext = new DataBaseContext(options, _httpContextAccessor);
        dbContext.Database.EnsureCreated();

        // Aktuelles Datum f�r die required ValidFrom-Eigenschaft
        var now = DateTime.Now;

        // Testdaten mit spezifischen Eigenschaften erstellen
        var clients = new List<Client>
    {
        new Client
        {
            Id = Guid.NewGuid(),
            Name = "M�ller",
            FirstName = "Hans",
            Company = "ABC GmbH",
            Addresses = new List<Address>
            {
                new Address {
                    Street = "Hauptstra�e 1",
                    City = "Berlin",
                    ValidFrom = now  // Wichtig: ValidFrom setzen
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
                    Street = "Nebenstra�e 2",
                    City = "M�nchen",
                    ValidFrom = now  // Wichtig: ValidFrom setzen
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
                    Street = "Bergstra�e 3",
                    City = "Hamburg",
                    ValidFrom = now  // Wichtig: ValidFrom setzen
                }
            }
        }
    };

        dbContext.Client.AddRange(clients);
        dbContext.SaveChanges();

        // Use real domain services for proper filtering behavior in integration tests
        var clientFilterService = new Klacks.Api.Domain.Services.Clients.ClientFilterService();
        var membershipFilterService = new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService(dbContext);
        var searchService = new Klacks.Api.Domain.Services.Clients.ClientSearchService();
        var sortingService = new Klacks.Api.Domain.Services.Clients.ClientSortingService();
        var changeTrackingService = new Klacks.Api.Domain.Services.Clients.ClientChangeTrackingService(dbContext, sortingService);
        var clientValidator = new Klacks.Api.Domain.Services.Clients.ClientValidator();
        var entityManagementService = new Klacks.Api.Domain.Services.Clients.ClientEntityManagementService(clientValidator);
        var workFilterService = new Klacks.Api.Domain.Services.Clients.ClientWorkFilterService();
        var collectionUpdateService = new Klacks.Api.Infrastructure.Services.EntityCollectionUpdateService(dbContext);

        var repository = new ClientRepository(dbContext, new MacroEngine(),
            changeTrackingService, entityManagementService, collectionUpdateService, clientValidator);

        var filterRepository = new ClientFilterRepository(dbContext, _clientGroupFilterService,
            clientFilterService, membershipFilterService, searchService, sortingService);

        // Zugriff auf die private Methode �ber Reflection
        var method = typeof(ClientRepository).GetMethod("FilterBySearchStringStandard",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        // Base Query
        var baseQuery = dbContext.Client
            .Include(c => c.Addresses)
            .AsNoTracking()
            .AsQueryable();

        // Test 1: Suche nach "m�ller hans"
        var result1 = method.Invoke(repository, new object[] {
        new string[] { "m�ller", "hans" },
        false,
        baseQuery
    }) as IQueryable<Client>;

        // Test 2: Suche nach "xyz ag"
        var result2 = method.Invoke(repository, new object[] {
        new string[] { "xyz", "ag" },
        false,
        baseQuery
    }) as IQueryable<Client>;

        // Test 3: Suche nach "berg" mit Adresseinbeziehung
        var result3 = method.Invoke(repository, new object[] {
        new string[] { "berg" },
        true,
        baseQuery
    }) as IQueryable<Client>;

        // Assert
        result1.Should().NotBeNull();
        result1.Count().Should().Be(1);
        result1.First().Name.Should().Be("M�ller");

        result2.Should().NotBeNull();
        result2.Count().Should().Be(1);
        result2.First().Name.Should().Be("Schmidt");

        result3.Should().NotBeNull();
        result3.Count().Should().Be(1);
        result3.First().Name.Should().Be("Schneider");
    }

    [SetUp]
    public void Setup()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddMaps(typeof(Klacks.Api.Application.AutoMapper.ClientMappingProfile).Assembly);
        });

        _mapper = config.CreateMapper();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _truncatedClient = FakeData.Clients.TruncatedClient();

        _clientGroupFilterService = Substitute.For<IClientGroupFilterService>();
        _clientGroupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
    }

    [TearDown]
    public void TearDown()
    {
        dbContext.Database.EnsureDeleted();
        dbContext.Dispose();
    }

    private void DataSeed(TruncatedClient truncated)
    {
        var clients = new List<Client>();
        var addresses = new List<Address>();
        var memberships = new List<Membership>();
        var communications = new List<Communication>();
        var annotations = new List<Annotation>();

        foreach (var item in truncated.Clients!)
        {
            if (item.Addresses.Any())
            {
                foreach (var address in item.Addresses)
                {
                    addresses.Add(address);
                }
                ;
            }
            if (item.Communications.Any())
            {
                foreach (var communication in communications)
                {
                    communications.Add(communication);
                }
            }
            if (item.Annotations.Any())
            {
                foreach (var annotation in annotations)
                {
                    annotations.Add(annotation);
                }
            }
            if (item.Membership != null)
            {
                item.Membership.ClientId = item.Id;
                memberships.Add(item.Membership);
            }
            else
            {
                var ms = new Membership { ClientId = item.Id, ValidFrom = DateTime.Now.AddMonths(-3) };
                memberships.Add(ms);
            }

            item.Addresses.Clear();
            item.Annotations.Clear();
            item.Communications.Clear();
            clients.Add(item);
        }

        dbContext.Client.AddRange(clients);
        dbContext.Address.AddRange(addresses);
        dbContext.Membership.AddRange(memberships);
        dbContext.Communication.AddRange(communications);
        dbContext.Annotation.AddRange(annotations);

        dbContext.SaveChanges();
    }
}