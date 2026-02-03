using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Presentation.DTOs.Filter;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class SettingsTokenServiceTests
{
    private IStateRepository _stateRepository;
    private ICountryRepository _countryRepository;
    private ILogger<SettingsTokenService> _logger;
    private SettingsTokenService _service;

    [SetUp]
    public void Setup()
    {
        _stateRepository = Substitute.For<IStateRepository>();
        _countryRepository = Substitute.For<ICountryRepository>();
        _logger = Substitute.For<ILogger<SettingsTokenService>>();
        _service = new SettingsTokenService(_stateRepository, _countryRepository, _logger);
    }

    [Test]
    public async Task GetRuleTokenListAsync_WithStatesAndCountries_ReturnsCorrectTokens()
    {
        // Arrange
        var countries = new List<Countries>
        {
            new Countries 
            { 
                Id = Guid.NewGuid(), 
                Abbreviation = "CH", 
                Name = new MultiLanguage { De = "Schweiz", En = "Switzerland" }
            },
            new Countries 
            { 
                Id = Guid.NewGuid(), 
                Abbreviation = "DE", 
                Name = new MultiLanguage { De = "Deutschland", En = "Germany" }
            }
        };

        var states = new List<State>
        {
            new State 
            { 
                Id = Guid.NewGuid(), 
                Abbreviation = "BE", 
                CountryPrefix = "CH",
                Name = new MultiLanguage { De = "Bern", En = "Bern" }
            },
            new State 
            { 
                Id = Guid.NewGuid(), 
                Abbreviation = "ZH", 
                CountryPrefix = "CH",
                Name = new MultiLanguage { De = "Zürich", En = "Zurich" }
            },
            new State 
            { 
                Id = Guid.NewGuid(), 
                Abbreviation = "BY", 
                CountryPrefix = "DE",
                Name = new MultiLanguage { De = "Bayern", En = "Bavaria" }
            }
        };

        _countryRepository.List().Returns(Task.FromResult(countries));
        _stateRepository.List().Returns(Task.FromResult(states));

        // Act
        var result = await _service.GetRuleTokenListAsync(true);
        var tokens = result.ToList();

        // Assert
        Assert.That(tokens.Count, Is.EqualTo(5)); // 3 states + 2 countries
        
        // Check state tokens have correct country
        var bernToken = tokens.FirstOrDefault(t => t.State == "BE");
        Assert.That(bernToken, Is.Not.Null);
        Assert.That(bernToken.Country, Is.EqualTo("CH"));
        Assert.That(bernToken.StateName.De, Is.EqualTo("Bern"));
        Assert.That(bernToken.CountryName.De, Is.EqualTo("Schweiz"));
        
        var zurichToken = tokens.FirstOrDefault(t => t.State == "ZH");
        Assert.That(zurichToken, Is.Not.Null);
        Assert.That(zurichToken.Country, Is.EqualTo("CH"));
        
        var bavariaToken = tokens.FirstOrDefault(t => t.State == "BY");
        Assert.That(bavariaToken, Is.Not.Null);
        Assert.That(bavariaToken.Country, Is.EqualTo("DE"));
        Assert.That(bavariaToken.CountryName.De, Is.EqualTo("Deutschland"));
        
        // Check country tokens
        var switzerlandToken = tokens.FirstOrDefault(t => t.State == "CH" && t.Country == "CH");
        Assert.That(switzerlandToken, Is.Not.Null);
        Assert.That(switzerlandToken.CountryName.De, Is.EqualTo("Schweiz"));
        
        var germanyToken = tokens.FirstOrDefault(t => t.State == "DE" && t.Country == "DE");
        Assert.That(germanyToken, Is.Not.Null);
        Assert.That(germanyToken.CountryName.De, Is.EqualTo("Deutschland"));
    }

    [Test]
    public async Task GetRuleTokenListAsync_WithSelectedFalse_SetsSelectToFalse()
    {
        // Arrange
        var countries = new List<Countries>
        {
            new Countries { Id = Guid.NewGuid(), Abbreviation = "CH", Name = new MultiLanguage() }
        };
        var states = new List<State>
        {
            new State { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = new MultiLanguage() }
        };

        _countryRepository.List().Returns(Task.FromResult(countries));
        _stateRepository.List().Returns(Task.FromResult(states));

        // Act
        var result = await _service.GetRuleTokenListAsync(false);
        var tokens = result.ToList();

        // Assert
        Assert.That(tokens.All(t => t.Select == false), Is.True);
    }

    [Test]
    public async Task GetRuleTokenListAsync_WithStateWithoutMatchingCountry_SkipsState()
    {
        // Arrange
        var countries = new List<Countries>
        {
            new Countries { Id = Guid.NewGuid(), Abbreviation = "DE", Name = new MultiLanguage() }
        };
        var states = new List<State>
        {
            new State { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = new MultiLanguage() }
        };

        _countryRepository.List().Returns(Task.FromResult(countries));
        _stateRepository.List().Returns(Task.FromResult(states));

        // Act
        var result = await _service.GetRuleTokenListAsync(true);
        var tokens = result.ToList();

        // Assert
        Assert.That(tokens.Count, Is.EqualTo(1)); // Only country token
        Assert.That(tokens.Any(t => t.State == "BE"), Is.False); // State not included
        Assert.That(tokens.Any(t => t.Country == "DE"), Is.True); // Country included
    }

    [Test]
    public async Task GetRuleTokenListAsync_WithEmptyLists_ReturnsEmptyList()
    {
        // Arrange
        _countryRepository.List().Returns(Task.FromResult(new List<Countries>()));
        _stateRepository.List().Returns(Task.FromResult(new List<State>()));

        // Act
        var result = await _service.GetRuleTokenListAsync(true);

        // Assert
        Assert.That(result.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task GetRuleTokenListAsync_AllSwissCantons_ReturnsCorrectTokens()
    {
        // Arrange
        var switzerland = new Countries 
        { 
            Id = Guid.NewGuid(), 
            Abbreviation = "CH", 
            Name = new MultiLanguage { De = "Schweiz" }
        };
        
        var swissCantons = new List<State>
        {
            new State { Id = Guid.NewGuid(), Abbreviation = "AG", CountryPrefix = "CH", Name = new MultiLanguage { De = "Aargau" }},
            new State { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = new MultiLanguage { De = "Bern" }},
            new State { Id = Guid.NewGuid(), Abbreviation = "ZH", CountryPrefix = "CH", Name = new MultiLanguage { De = "Zürich" }}
        };

        _countryRepository.List().Returns(Task.FromResult(new List<Countries> { switzerland }));
        _stateRepository.List().Returns(Task.FromResult(swissCantons));

        // Act
        var result = await _service.GetRuleTokenListAsync(true);
        var tokens = result.ToList();

        // Assert
        Assert.That(tokens.Count, Is.EqualTo(4)); // 3 cantons + 1 country
        
        var cantonTokens = tokens.Where(t => t.State != "CH").ToList();
        Assert.That(cantonTokens.Count, Is.EqualTo(3));
        Assert.That(cantonTokens.All(t => t.Country == "CH"), Is.True);
        
        var countryToken = tokens.FirstOrDefault(t => t.State == "CH" && t.Country == "CH");
        Assert.That(countryToken, Is.Not.Null);
    }
}