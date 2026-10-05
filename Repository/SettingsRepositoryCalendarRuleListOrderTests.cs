using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Repository;

[TestFixture]
internal class SettingsRepositoryCalendarRuleListOrderTests
{
    private const string COUNTRY_CH = "CH";
    private const string COUNTRY_US = "US";
    private const string STATE_ALL = "ALL";
    private const string STATE_ZH = "ZH";
    private const string RULE_CHRISTMAS = "12/25";
    private const string RULE_NEW_YEAR = "01/01";
    private const string RULE_EASTER = "EASTER";

    private DataBaseContext _dbContext = null!;
    private SettingsRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        _repository = new SettingsRepository(
            _dbContext,
            Substitute.For<ICalendarRuleFilterService>(),
            Substitute.For<ICalendarRuleSortingService>(),
            Substitute.For<ICalendarRulePaginationService>(),
            Substitute.For<IMacroManagementService>());
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [Test]
    public async Task GetCalendarRuleList_OrdersByCountryStateRuleThenId_RegardlessOfInsertionOrder()
    {
        var lowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var highId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        _dbContext.CalendarRule.AddRange(
            NewRule(Guid.NewGuid(), COUNTRY_US, STATE_ALL, RULE_CHRISTMAS),
            NewRule(Guid.NewGuid(), COUNTRY_CH, STATE_ZH, RULE_EASTER),
            NewRule(highId, COUNTRY_CH, STATE_ALL, RULE_CHRISTMAS),
            NewRule(Guid.NewGuid(), COUNTRY_CH, STATE_ALL, RULE_NEW_YEAR),
            NewRule(lowId, COUNTRY_CH, STATE_ALL, RULE_CHRISTMAS));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetCalendarRuleList();

        result.Select(r => (r.Country, r.State, r.Rule)).ShouldBe(new[]
        {
            (COUNTRY_CH, STATE_ALL, RULE_NEW_YEAR),
            (COUNTRY_CH, STATE_ALL, RULE_CHRISTMAS),
            (COUNTRY_CH, STATE_ALL, RULE_CHRISTMAS),
            (COUNTRY_CH, STATE_ZH, RULE_EASTER),
            (COUNTRY_US, STATE_ALL, RULE_CHRISTMAS),
        });
        var sameRule = result.Where(r => r.Country == COUNTRY_CH && r.State == STATE_ALL && r.Rule == RULE_CHRISTMAS).ToList();
        sameRule.Select(r => r.Id).ShouldBe(new[] { lowId, highId });
    }

    private static CalendarRule NewRule(Guid id, string country, string state, string rule)
    {
        return new CalendarRule
        {
            Id = id,
            Country = country,
            State = state,
            Rule = rule,
            IsMandatory = true,
            IsPaid = true,
        };
    }
}
