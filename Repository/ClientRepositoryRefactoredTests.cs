using FluentAssertions;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Presentation.DTOs.Filter;
using Klacks.Api.Presentation.DTOs.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace UnitTest.Repository;

[TestFixture]
public class ClientRepositoryRefactoredTests
{
    private DataBaseContext _context;
    private IClientRepository _clientRepository;
    private IClientFilterRepository _clientFilterRepository;
    private IClientBreakRepository _clientBreakRepository;
    private IClientWorkRepository _clientWorkRepository;
    private IClientSearchFilterService _mockSearchFilterService;
    private IMacroEngine _mockMacroEngine;
    private IGetAllClientIdsFromGroupAndSubgroups _mockGroupClient;
    private IGroupVisibilityService _mockGroupVisibility;
    private IClientFilterService _mockClientFilterService;
    private IClientMembershipFilterService _mockMembershipFilterService;
    private IClientSearchService _mockSearchService;
    private IClientSortingService _mockSortingService;
    private IClientChangeTrackingService _mockChangeTrackingService;
    private IClientEntityManagementService _mockEntityManagementService;
    private IClientWorkFilterService _mockWorkFilterService;
    private IClientValidator _mockClientValidator;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        _mockMacroEngine = Substitute.For<IMacroEngine>();
        _mockGroupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _mockGroupVisibility = Substitute.For<IGroupVisibilityService>();
        _mockClientFilterService = Substitute.For<IClientFilterService>();
        _mockMembershipFilterService = Substitute.For<IClientMembershipFilterService>();
        _mockSearchService = Substitute.For<IClientSearchService>();
        _mockSortingService = Substitute.For<IClientSortingService>();
        _mockChangeTrackingService = Substitute.For<IClientChangeTrackingService>();
        _mockEntityManagementService = Substitute.For<IClientEntityManagementService>();
        _mockWorkFilterService = Substitute.For<IClientWorkFilterService>();
        _mockClientValidator = Substitute.For<IClientValidator>();

        var mockGroupFilterService = Substitute.For<IClientGroupFilterService>();
        _mockSearchFilterService = Substitute.For<IClientSearchFilterService>();
        mockGroupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        _mockSearchFilterService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);
        
        var collectionUpdateService = new Klacks.Api.Infrastructure.Services.EntityCollectionUpdateService(_context);

        var mockLogger = Substitute.For<ILogger<ClientRepository>>();

        _clientRepository = new ClientRepository(
            _context,
            _mockMacroEngine,
            _mockChangeTrackingService,
            _mockEntityManagementService,
            collectionUpdateService,
            _mockClientValidator,
            mockLogger);

        _clientFilterRepository = new ClientFilterRepository(
            _context,
            mockGroupFilterService,
            _mockClientFilterService,
            _mockMembershipFilterService,
            _mockSearchService,
            _mockSortingService);

        _clientBreakRepository = new ClientBreakRepository(
            _context,
            mockGroupFilterService,
            _mockSearchFilterService);

        _clientWorkRepository = new ClientWorkRepository(
            _context,
            mockGroupFilterService,
            _mockSearchFilterService);

        await CreateTestData();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private async Task CreateTestData()
    {
        var today = DateTime.Now;
        var clients = new List<Client>
        {
            new Client 
            { 
                Id = Guid.NewGuid(), 
                Name = "Müller", 
                FirstName = "Hans",
                IdNumber = 123,
                Gender = GenderEnum.Male,
                LegalEntity = false,
                Membership = new Membership
                {
                    ValidFrom = today.AddDays(-30),
                    ValidUntil = today.AddDays(30)
                }
            },
            new Client 
            { 
                Id = Guid.NewGuid(), 
                Name = "Schmidt", 
                FirstName = "Anna",
                IdNumber = 456,
                Gender = GenderEnum.Female,
                LegalEntity = false,
                Membership = new Membership
                {
                    ValidFrom = today.AddDays(-60),
                    ValidUntil = today.AddDays(-10)
                }
            }
        };

        await _context.Client.AddRangeAsync(clients);
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task BreakList_ShouldUseDomainServices()
    {
        //Arrange
        var filter = new Klacks.Api.Domain.Models.Filters.BreakFilter
        {
            SearchString = "Hans",
            CurrentYear = DateTime.Now.Year,
            AbsenceIds = new List<Guid>()
        };
        var pagination = new Klacks.Api.Domain.Models.Filters.PaginationParams
        {
            PageIndex = 0,
            PageSize = 100
        };

        var testClients = _context.Client.AsQueryable();
        
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));

        _mockSearchService.IsNumericSearch("Hans").Returns(false);
        _mockSearchService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Hans", false)
            .Returns(testClients.Where(c => c.FirstName.Contains("Hans")));

        //Act
        var result = await _clientBreakRepository.BreakList(filter);

        //Assert
        result.Should().NotBeNull();
        
        _mockSearchFilterService.Received(1).ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Hans", false);
    }

    [Test]
    public async Task BreakList_WithNumericSearch_ShouldUseIdNumberSearch()
    {
        //Arrange
        var filter = new Klacks.Api.Domain.Models.Filters.BreakFilter
        {
            SearchString = "123",
            CurrentYear = DateTime.Now.Year,
            AbsenceIds = new List<Guid>()
        };
        var pagination = new Klacks.Api.Domain.Models.Filters.PaginationParams
        {
            PageIndex = 0,
            PageSize = 100
        };

        var testClients = _context.Client.AsQueryable();
        
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        //Act
        var result = await _clientBreakRepository.BreakList(filter);

        //Assert
        result.Should().NotBeNull();
        
        _mockSearchFilterService.Received(1).ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "123", false);
    }

    [Test]
    public async Task WorkList_ShouldUseDomainServices()
    {
        //Arrange
        var filter = new Klacks.Api.Domain.Models.Filters.WorkFilter
        {
            SearchString = "Anna",
            CurrentYear = DateTime.Now.Year,
            CurrentMonth = DateTime.Now.Month,
            DayVisibleBeforeMonth = 5,
            DayVisibleAfterMonth = 5
        };
        var pagination = new Klacks.Api.Domain.Models.Filters.PaginationParams
        {
            PageIndex = 0,
            PageSize = 100
        };

        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        _mockSearchService.IsNumericSearch("Anna").Returns(false);
        _mockSearchService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Anna", false)
            .Returns(args => (IQueryable<Client>)args[0]);
        
        _mockWorkFilterService.FilterByWorkSchedule(Arg.Any<IQueryable<Client>>(), Arg.Any<WorkFilter>(), Arg.Any<DataBaseContext>())
            .Returns(args => (IQueryable<Client>)args[0]);

        //Act
        var result = await _clientWorkRepository.WorkList(filter);

        //Assert
        result.Should().NotBeNull();
        
        _mockSearchFilterService.Received(1).ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Anna", false);
    }

    [Test]
    public async Task FilterClients_WithComplexFilter_ShouldUseDomainServices()
    {
        //Arrange
        var filter = new Klacks.Api.Domain.Models.Filters.ClientFilter
        {
            SearchString = "Test",
            Male = true,
            Female = false,
            LegalEntity = false,
            HasAnnotation = true,
            ActiveMembership = true,
            OrderBy = "name",
            SortOrder = "asc",
            FilteredStateToken = new List<Klacks.Api.Domain.Models.Filters.StateCountryFilter>(),
            Countries = new List<string>()
        };

        var testClients = _context.Client.AsQueryable();
        
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));

        _mockSearchService.IsNumericSearch("Test").Returns(false);
        _mockSearchService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Test", Arg.Any<bool>())
            .Returns(testClients);
        _mockClientFilterService.ApplyGenderFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<int[]>())
            .Returns(testClients);
        _mockClientFilterService.ApplyAnnotationFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<bool?>())
            .Returns(testClients);
        _mockClientFilterService.ApplyAddressTypeFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<int[]>())
            .Returns(testClients);
        _mockClientFilterService.ApplyStateOrCountryFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<List<Klacks.Api.Domain.Models.Filters.StateCountryFilter>>(), Arg.Any<List<string>>())
            .Returns(testClients);
        _mockMembershipFilterService.ApplyMembershipFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(testClients);
        _mockSortingService.ApplySorting(Arg.Any<IQueryable<Client>>(), "name", "asc")
            .Returns(testClients.OrderBy(c => c.Name));

        //Act
        var result = await _clientFilterRepository.FilterClients(filter);
        var clients = result.ToList();

        //Assert
        clients.Should().NotBeNull();
        
        _mockSearchService.Received(1).ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Test", Arg.Any<bool>());
        _mockClientFilterService.Received(1).ApplyGenderFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<int[]>());
        _mockClientFilterService.Received(1).ApplyAnnotationFilter(Arg.Any<IQueryable<Client>>(), true);
        _mockMembershipFilterService.Received(1).ApplyMembershipFilter(Arg.Any<IQueryable<Client>>(), true, false, false);
        _mockSortingService.Received(1).ApplySorting(Arg.Any<IQueryable<Client>>(), "name", "asc");
    }

    [Test]
    public async Task Repository_ShouldPreferDomainServicesOverDirectQueries()
    {
        //Arrange
        var filter = new Klacks.Api.Domain.Models.Filters.BreakFilter
        {
            SearchString = "test",
            CurrentYear = 2024,
            AbsenceIds = new List<Guid>()
        };
        var pagination = new Klacks.Api.Domain.Models.Filters.PaginationParams
        {
            PageIndex = 0,
            PageSize = 100
        };

        var testClients = _context.Client.AsQueryable();
        
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        //Act
        await _clientBreakRepository.BreakList(filter);

        //Assert
        _mockSearchFilterService.Received().ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>());

    }
}