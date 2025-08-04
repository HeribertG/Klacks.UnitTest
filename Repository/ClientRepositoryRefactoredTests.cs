using FluentAssertions;
using Klacks.Api.Datas;
using Klacks.Api.Enums;
using Klacks.Api.Interfaces;
using Klacks.Api.Interfaces.Domains;
using Klacks.Api.Models.Associations;
using Klacks.Api.Models.Staffs;
using Klacks.Api.Repositories;
using Klacks.Api.Resources.Filter;
using Klacks.Api.Resources.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace UnitTest.Repository;

[TestFixture]
public class ClientRepositoryRefactoredTests
{
    private DataBaseContext _context;
    private IClientRepository _clientRepository;
    private IMacroEngine _mockMacroEngine;
    private IGetAllClientIdsFromGroupAndSubgroups _mockGroupClient;
    private IGroupVisibilityService _mockGroupVisibility;
    private IClientFilterService _mockClientFilterService;
    private IClientMembershipFilterService _mockMembershipFilterService;
    private IClientSearchService _mockSearchService;
    private IClientSortingService _mockSortingService;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        // Create mocks for domain services
        _mockMacroEngine = Substitute.For<IMacroEngine>();
        _mockGroupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _mockGroupVisibility = Substitute.For<IGroupVisibilityService>();
        _mockClientFilterService = Substitute.For<IClientFilterService>();
        _mockMembershipFilterService = Substitute.For<IClientMembershipFilterService>();
        _mockSearchService = Substitute.For<IClientSearchService>();
        _mockSortingService = Substitute.For<IClientSortingService>();

        // Create repository with mocked domain services
        _clientRepository = new ClientRepository(
            _context,
            _mockMacroEngine,
            _mockGroupClient,
            _mockGroupVisibility,
            _mockClientFilterService,
            _mockMembershipFilterService,
            _mockSearchService,
            _mockSortingService);

        // Create test data
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
        // Arrange
        var filter = new BreakFilter
        {
            Search = "Hans",
            CurrentYear = DateTime.Now.Year,
            OrderBy = "name",
            SortOrder = "asc",
            Absences = new List<AbsenceTokenFilter>()
        };

        var testClients = _context.Client.AsQueryable();
        
        // Setup mock responses for group services first
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        // Setup domain service mocks
        _mockSearchService.IsNumericSearch("Hans").Returns(false);
        _mockSearchService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Hans", false)
            .Returns(testClients.Where(c => c.FirstName.Contains("Hans")));
        _mockMembershipFilterService.ApplyMembershipYearFilter(Arg.Any<IQueryable<Client>>(), filter)
            .Returns(testClients);
        _mockMembershipFilterService.ApplyBreaksYearFilter(Arg.Any<IQueryable<Client>>(), filter)
            .Returns(testClients);
        _mockSortingService.ApplySorting(Arg.Any<IQueryable<Client>>(), "name", "asc")
            .Returns(testClients.OrderBy(c => c.Name));

        // Act
        var result = await _clientRepository.BreakList(filter);

        // Assert
        result.Should().NotBeNull();
        
        // Verify that domain services were called
        _mockSearchService.Received(1).IsNumericSearch("Hans");
        _mockSearchService.Received(1).ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Hans", false);
        _mockMembershipFilterService.Received(1).ApplyMembershipYearFilter(Arg.Any<IQueryable<Client>>(), filter);
        _mockMembershipFilterService.Received(1).ApplyBreaksYearFilter(Arg.Any<IQueryable<Client>>(), filter);
        _mockSortingService.Received(1).ApplySorting(Arg.Any<IQueryable<Client>>(), "name", "asc");
    }

    [Test]
    public async Task BreakList_WithNumericSearch_ShouldUseIdNumberSearch()
    {
        // Arrange
        var filter = new BreakFilter
        {
            Search = "123",
            CurrentYear = DateTime.Now.Year,
            OrderBy = "name",
            SortOrder = "asc",
            Absences = new List<AbsenceTokenFilter>()
        };

        var testClients = _context.Client.AsQueryable();
        
        // Setup mock responses for group services first
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        // Setup domain service mocks
        _mockSearchService.IsNumericSearch("123").Returns(true);
        _mockSearchService.ApplyIdNumberSearch(Arg.Any<IQueryable<Client>>(), 123)
            .Returns(testClients.Where(c => c.IdNumber == 123));
        _mockMembershipFilterService.ApplyMembershipYearFilter(Arg.Any<IQueryable<Client>>(), filter)
            .Returns(testClients);
        _mockMembershipFilterService.ApplyBreaksYearFilter(Arg.Any<IQueryable<Client>>(), filter)
            .Returns(testClients);
        _mockSortingService.ApplySorting(Arg.Any<IQueryable<Client>>(), "name", "asc")
            .Returns(testClients.OrderBy(c => c.Name));

        // Act
        var result = await _clientRepository.BreakList(filter);

        // Assert
        result.Should().NotBeNull();
        
        // Verify that numeric search was used
        _mockSearchService.Received(1).IsNumericSearch("123");
        _mockSearchService.Received(1).ApplyIdNumberSearch(Arg.Any<IQueryable<Client>>(), 123);
        _mockSearchService.DidNotReceive().ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task WorkList_ShouldUseDomainServices()
    {
        // Arrange
        var filter = new WorkFilter
        {
            Search = "Anna",
            CurrentYear = DateTime.Now.Year,
            CurrentMonth = DateTime.Now.Month,
            OrderBy = "firstname",
            SortOrder = "desc"
        };

        var testClients = _context.Client.AsQueryable();
        
        // Setup mock responses for group services first
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        // Setup domain service mocks
        _mockSearchService.IsNumericSearch("Anna").Returns(false);
        _mockSearchService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Anna", false)
            .Returns(testClients.Where(c => c.FirstName.Contains("Anna")));
        _mockSortingService.ApplySorting(Arg.Any<IQueryable<Client>>(), "firstname", "desc")
            .Returns(testClients.OrderByDescending(c => c.FirstName));

        // Act
        var result = await _clientRepository.WorkList(filter);

        // Assert
        result.Should().NotBeNull();
        
        // Verify that domain services were called
        _mockSearchService.Received(1).IsNumericSearch("Anna");
        _mockSearchService.Received(1).ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Anna", false);
        _mockSortingService.Received(1).ApplySorting(Arg.Any<IQueryable<Client>>(), "firstname", "desc");
    }

    [Test]
    public async Task FilterClients_WithComplexFilter_ShouldUseDomainServices()
    {
        // Arrange
        var filter = new FilterResource
        {
            SearchString = "Test",
            Male = true,
            Female = false,
            LegalEntity = false,
            HasAnnotation = true,
            ActiveMembership = true,
            OrderBy = "name",
            SortOrder = "asc"
        };

        var testClients = _context.Client.AsQueryable();
        
        // Setup mock responses for group services first
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        // Setup mock responses for domain services
        _mockSearchService.IsNumericSearch("Test").Returns(false);
        _mockSearchService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Test", Arg.Any<bool>())
            .Returns(testClients);
        _mockClientFilterService.ApplyGenderFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<int[]>(), Arg.Any<bool?>())
            .Returns(testClients);
        _mockClientFilterService.ApplyAnnotationFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<bool?>())
            .Returns(testClients);
        _mockClientFilterService.ApplyAddressTypeFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<int[]>())
            .Returns(testClients);
        _mockClientFilterService.ApplyStateOrCountryFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<List<StateCountryToken>>(), Arg.Any<List<Klacks.Api.Resources.Settings.CountryResource>>())
            .Returns(testClients);
        _mockMembershipFilterService.ApplyMembershipFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(testClients);
        _mockSortingService.ApplySorting(Arg.Any<IQueryable<Client>>(), "name", "asc")
            .Returns(testClients.OrderBy(c => c.Name));

        // Act
        var result = await _clientRepository.FilterClients(filter);
        var clients = await result.ToListAsync();

        // Assert
        clients.Should().NotBeNull();
        
        // Verify that domain services were called
        _mockSearchService.Received(1).ApplySearchFilter(Arg.Any<IQueryable<Client>>(), "Test", Arg.Any<bool>());
        _mockClientFilterService.Received(1).ApplyGenderFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<int[]>(), Arg.Any<bool?>());
        _mockClientFilterService.Received(1).ApplyAnnotationFilter(Arg.Any<IQueryable<Client>>(), true);
        _mockMembershipFilterService.Received(1).ApplyMembershipFilter(Arg.Any<IQueryable<Client>>(), true, false, false);
        _mockSortingService.Received(1).ApplySorting(Arg.Any<IQueryable<Client>>(), "name", "asc");
    }

    [Test]
    public async Task Repository_ShouldPreferDomainServicesOverDirectQueries()
    {
        // This test verifies that the refactored repository uses domain services
        // instead of direct LINQ queries for business logic

        // Arrange
        var filter = new BreakFilter
        {
            Search = "test",
            CurrentYear = 2024,
            OrderBy = "name",
            SortOrder = "asc",
            Absences = new List<AbsenceTokenFilter>()
        };

        var testClients = _context.Client.AsQueryable();
        
        // Setup mock responses for group services first
        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
            .Returns(Task.FromResult(new List<Guid>()));
        _mockGroupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new List<Guid>()));
        
        // Setup all required domain service mocks
        _mockSearchService.IsNumericSearch(Arg.Any<string>()).Returns(false);
        _mockSearchService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(testClients);
        _mockMembershipFilterService.ApplyMembershipYearFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<BreakFilter>())
            .Returns(testClients);
        _mockMembershipFilterService.ApplyBreaksYearFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<BreakFilter>())
            .Returns(testClients);
        _mockSortingService.ApplySorting(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(testClients);

        // Act
        await _clientRepository.BreakList(filter);

        // Assert - Verify that all domain services were used
        _mockSearchService.Received().IsNumericSearch(Arg.Any<string>());
        _mockSearchService.Received().ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>());
        _mockMembershipFilterService.Received().ApplyMembershipYearFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<BreakFilter>());
        _mockMembershipFilterService.Received().ApplyBreaksYearFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<BreakFilter>());
        _mockSortingService.Received().ApplySorting(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<string>());

        Console.WriteLine("✅ ClientRepository successfully uses Domain Services instead of direct business logic");
    }
}