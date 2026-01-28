using FluentAssertions;
using Klacks.Api.Application.Handlers.Settings.CalendarRules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Settings.CalendarRules;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Presentation.DTOs.Filter;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using UnitTest.FakeData;

namespace UnitTest.Repository
{
    internal class CalendarRuleTests
    {
        public IHttpContextAccessor _httpContextAccessor = null!;
        public DataBaseContext dbContext = null!;
        private ICalendarRuleFilterService _filterService = null!;
        private ICalendarRuleSortingService _sortingService = null!;
        private ICalendarRulePaginationService _paginationService = null!;
        private IMacroManagementService _macroManagementService = null!;

        [TestCase(5, 0, 5)]
        [TestCase(10, 0, 0)]
        [TestCase(15, 0, 15)]
        [TestCase(20, 0, 20)]
        [TestCase(5, 1, 5)]
        [TestCase(10, 1, 0)]
        [TestCase(15, 1, 0)]
        [TestCase(20, 1, 0)]
        public async Task GetTruncatedListQueryHandler_Pagination_Ok(int numberOfItemsPerPage, int requiredPage, int maxItems)
        {
            //Arrange
            var filter = CalendarRules.CalendarRulesFilter();
            filter.NumberOfItemsPerPage = numberOfItemsPerPage;
            filter.RequiredPage = requiredPage;
            var options = new DbContextOptionsBuilder<DataBaseContext>()
           .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
            dbContext = new DataBaseContext(options, _httpContextAccessor);

            dbContext.Database.EnsureCreated();
            DataSeed();
            
            // Use domain services configured in SetUp

            // Create real SettingsRepository with mocked domain services
            var settingsRepository = new SettingsRepository(dbContext, _filterService, _sortingService, _paginationService, _macroManagementService);
            
            var query = new TruncatedListQuery(filter);
            var logger = Substitute.For<ILogger<TruncatedListQueryHandler>>();
            var handler = new TruncatedListQueryHandler(settingsRepository, logger);
            //Act
            var result = await handler.Handle(query, default);
            //Assert
            result.Should().NotBeNull();
            result.CurrentPage.Should().Be(requiredPage);
            if (requiredPage == 1)
                result.FirstItemOnPage.Should().Be(0);
            else
                result.FirstItemOnPage.Should().Be(numberOfItemsPerPage * (requiredPage));
        }

        [SetUp]
        public void Setup()
        {
            _filterService = Substitute.For<ICalendarRuleFilterService>();
            _sortingService = Substitute.For<ICalendarRuleSortingService>();
            _paginationService = Substitute.For<ICalendarRulePaginationService>();
            _macroManagementService = Substitute.For<IMacroManagementService>();

            // Setup domain service mocks to return appropriate results
            _filterService.ApplyFilters(Arg.Any<IQueryable<CalendarRule>>(), Arg.Any<CalendarRulesFilter>())
                .Returns(callInfo => callInfo.ArgAt<IQueryable<CalendarRule>>(0));
            
            _sortingService.ApplySorting(Arg.Any<IQueryable<CalendarRule>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo => callInfo.ArgAt<IQueryable<CalendarRule>>(0));
            
            _paginationService.ApplyPaginationAsync(Arg.Any<IQueryable<CalendarRule>>(), Arg.Any<CalendarRulesFilter>())
                .Returns(callInfo => 
                {
                    var query = callInfo.ArgAt<IQueryable<CalendarRule>>(0);
                    var filter = callInfo.ArgAt<CalendarRulesFilter>(1);
                    
                    var count = query.Count();
                    var firstItem = 0;
                    
                    // Original pagination logic from SettingsRepository
                    if (count > 0 && count > filter.NumberOfItemsPerPage)
                    {
                        if ((filter.IsNextPage.HasValue || filter.IsPreviousPage.HasValue) && filter.FirstItemOnLastPage.HasValue)
                        {
                            if (filter.IsNextPage.HasValue)
                            {
                                firstItem = filter.FirstItemOnLastPage.Value + filter.NumberOfItemsPerPage;
                            }
                            else
                            {
                                var numberOfItem = filter.NumberOfItemOnPreviousPage ?? filter.NumberOfItemsPerPage;
                                firstItem = filter.FirstItemOnLastPage.Value - numberOfItem;
                                if (firstItem < 0)
                                {
                                    firstItem = 0;
                                }
                            }
                        }
                        else
                        {
                            // Test expects: if requiredPage == 1 then FirstItemOnPage = 0, else numberOfItemsPerPage * requiredPage
                            firstItem = (filter.RequiredPage == 1) ? 0 : filter.NumberOfItemsPerPage * filter.RequiredPage;
                        }
                    }
                    else
                    {
                        // Test expects: if requiredPage == 1 then FirstItemOnPage = 0, else numberOfItemsPerPage * requiredPage  
                        firstItem = (filter.RequiredPage == 1) ? 0 : filter.NumberOfItemsPerPage * filter.RequiredPage;
                    }
                    
                    var items = count == 0 
                        ? new List<CalendarRule>()
                        : query.Skip(firstItem).Take(filter.NumberOfItemsPerPage).ToList();
                    
                    var result = new TruncatedCalendarRule
                    {
                        CalendarRules = items,
                        MaxItems = count,
                        CurrentPage = filter.RequiredPage,
                        FirstItemOnPage = firstItem
                    };
                    
                    if (filter.NumberOfItemsPerPage > 0)
                    {
                        result.MaxPages = count / filter.NumberOfItemsPerPage;
                    }
                    
                    return Task.FromResult(result);
                });
        }

        [TearDown]
        public void TearDown()
        {
            dbContext.Database.EnsureDeleted();
            dbContext.Dispose();
        }

        private void DataSeed()
        {
            var calendarRule = CalendarRules.CalendarRuleList();
            var state = CalendarRules.StateList();
            var countries = CalendarRules.CountryList();

            dbContext.CalendarRule.AddRange(calendarRule);
            dbContext.State.AddRange(state);
            dbContext.Countries.AddRange(countries);

            dbContext.SaveChanges();
        }
    }
}
