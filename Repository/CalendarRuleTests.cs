using AutoMapper;
using FluentAssertions;
using Klacks.Api.Application.Handlers.Settings.CalendarRules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Settings.CalendarRules;
using Klacks.Api.Application.Services;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using MediatR;
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
        private IMapper _mapper = null!;
        private IMediator _mediator = null!;
        private ILogger<SettingsApplicationService> _logger = null!;

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
            
            // Create real repositories
            var settingsRepository = new SettingsRepository(dbContext);
            var stateRepository = new StateRepository(dbContext, Substitute.For<ILogger<State>>());
            var countryRepository = new CountryRepository(dbContext, Substitute.For<ILogger<Countries>>());
            
            // Create SettingsApplicationService with real repositories and mocked dependencies
            var settingsApplicationService = new SettingsApplicationService(
                settingsRepository,
                stateRepository, 
                countryRepository,
                _mapper,
                _logger
            );
            
            var query = new TruncatedListQuery(filter);
            var handler = new TruncatedListQueryHandler(settingsApplicationService);
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
            _mapper = Substitute.For<IMapper>();
            _mediator = Substitute.For<IMediator>();
            _logger = Substitute.For<ILogger<SettingsApplicationService>>();
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
