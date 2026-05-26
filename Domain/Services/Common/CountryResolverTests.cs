// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CountryResolver — resolve by ISO code, by German name, by English name,
/// unknown input returns null, and GetDefaultAsync reads APP_ADDRESS_COUNTRY.
/// </summary>

using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Common;

namespace Klacks.UnitTest.Domain.Services.Common;

[TestFixture]
public class CountryResolverTests
{
    private ICountryRepository _countryRepository = null!;
    private ISettingsReader _settingsReader = null!;
    private CountryResolver _resolver = null!;

    private static Countries MakeCountry(string abbr, string prefix, string nameDe, string nameEn) => new()
    {
        Abbreviation = abbr,
        Prefix = prefix,
        Name = new MultiLanguage { De = nameDe, En = nameEn }
    };

    [SetUp]
    public void Setup()
    {
        _countryRepository = Substitute.For<ICountryRepository>();
        _settingsReader = Substitute.For<ISettingsReader>();

        var countries = new List<Countries>
        {
            MakeCountry("CH", "+41", "Schweiz", "Switzerland"),
            MakeCountry("DE", "+49", "Deutschland", "Germany"),
            MakeCountry("AT", "+43", "Österreich", "Austria")
        };

        _countryRepository.List().Returns(countries);
        _resolver = new CountryResolver(_countryRepository, _settingsReader);
    }

    [Test]
    public async Task ResolveAsync_ByIsoCode_ReturnsMatch()
    {
        var result = await _resolver.ResolveAsync("CH");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Abbreviation, Is.EqualTo("CH"));
        Assert.That(result.Prefix, Is.EqualTo("+41"));
    }

    [Test]
    public async Task ResolveAsync_ByIsoCode_CaseInsensitive()
    {
        var result = await _resolver.ResolveAsync("ch");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Abbreviation, Is.EqualTo("CH"));
    }

    [Test]
    public async Task ResolveAsync_ByGermanName_ReturnsMatch()
    {
        var result = await _resolver.ResolveAsync("Schweiz");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Abbreviation, Is.EqualTo("CH"));
    }

    [Test]
    public async Task ResolveAsync_ByEnglishName_ReturnsMatch()
    {
        var result = await _resolver.ResolveAsync("Switzerland");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Abbreviation, Is.EqualTo("CH"));
    }

    [Test]
    public async Task ResolveAsync_UnknownInput_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync("Atlantis");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_NullInput_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync(null);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_EmptyInput_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync(string.Empty);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDefaultAsync_ReadsSetting_AndResolvesToCountry()
    {
        var setting = new Klacks.Api.Domain.Models.Settings.Settings { Value = "CH" };
        _settingsReader.GetSetting("APP_ADDRESS_COUNTRY").Returns(setting);

        var result = await _resolver.GetDefaultAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Abbreviation, Is.EqualTo("CH"));
        Assert.That(result.Prefix, Is.EqualTo("+41"));
    }

    [Test]
    public async Task GetDefaultAsync_MissingSetting_ReturnsNull()
    {
        _settingsReader.GetSetting("APP_ADDRESS_COUNTRY").Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        var result = await _resolver.GetDefaultAsync();

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDefaultAsync_UnresolvableSettingValue_ReturnsNull()
    {
        var setting = new Klacks.Api.Domain.Models.Settings.Settings { Value = "XY" };
        _settingsReader.GetSetting("APP_ADDRESS_COUNTRY").Returns(setting);

        var result = await _resolver.GetDefaultAsync();

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ListCalledOnce_WhenResolvedTwice()
    {
        await _resolver.ResolveAsync("CH");
        await _resolver.ResolveAsync("DE");

        await _countryRepository.Received(1).List();
    }
}
