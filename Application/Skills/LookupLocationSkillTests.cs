// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for LookupLocationSkill: the postcode directory holds Swiss postcodes only, so results are labelled
/// as Switzerland whatever the installation's default country is, and the result says what the directory covers.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Settings;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class LookupLocationSkillTests
{
    private const string SwissNameDe = "Schweiz";
    private const string AustrianNameDe = "Österreich";
    private const string AustriaCode = "AT";
    private const int LausanneZip = 1010;
    private const string LausanneCity = "Lausanne";
    private const string VaudCanton = "VD";

    private IPostcodeChRepository _postcodeRepository = null!;
    private ICountryResolver _countryResolver = null!;
    private LookupLocationSkill _skill = null!;

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "planner",
        UserPermissions = new List<string>()
    };

    private static Countries Country(string abbreviation, string nameDe) => new()
    {
        Abbreviation = abbreviation,
        Name = new MultiLanguage { De = nameDe, En = nameDe }
    };

    [SetUp]
    public void SetUp()
    {
        _postcodeRepository = Substitute.For<IPostcodeChRepository>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(Country(AustriaCode, AustrianNameDe));
        _countryResolver.ResolveAsync(CountryCodes.Switzerland, Arg.Any<CancellationToken>())
            .Returns(Country(CountryCodes.Switzerland, SwissNameDe));
        _postcodeRepository.GetByZipAsync(LausanneZip)
            .Returns(new List<PostcodeCH> { new() { Zip = LausanneZip, City = LausanneCity, State = VaudCanton } });
        _skill = new LookupLocationSkill(_postcodeRepository, _countryResolver);
    }

    [Test]
    public async Task ExecuteAsync_AustrianInstallation_LabelsTheSwissHitAsSwitzerland()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { ["zip"] = LausanneZip });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain($"{LausanneCity} is in canton {VaudCanton}, {SwissNameDe}");
        result.Message.ShouldNotContain(AustrianNameDe);
        var country = result.Data!.GetType().GetProperty("DirectoryCountry")!.GetValue(result.Data);
        country.ShouldBe(SwissNameDe);
    }

    [Test]
    public async Task ExecuteAsync_Hit_StatesThatTheDirectoryCoversSwitzerlandOnly()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { ["zip"] = LausanneZip });

        result.Message.ShouldContain($"The postcode directory covers {SwissNameDe} only.");
    }

    [Test]
    public async Task ExecuteAsync_CitySearchWithSeveralHits_StatesTheCoverageToo()
    {
        _postcodeRepository.GetByCityAsync("Bern").Returns(new List<PostcodeCH>
        {
            new() { Zip = 3000, City = "Bern", State = "BE" },
            new() { Zip = 3001, City = "Bern 1", State = "BE" }
        });

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { ["city"] = "Bern" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Found 2 location(s)");
        result.Message.ShouldContain($"The postcode directory covers {SwissNameDe} only.");
    }

    [Test]
    public async Task ExecuteAsync_SwissCountryMissingInTheCountryTable_FallsBackToTheCountryCode()
    {
        _countryResolver.ResolveAsync(CountryCodes.Switzerland, Arg.Any<CancellationToken>())
            .Returns((Countries?)null);

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { ["zip"] = LausanneZip });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain($"{LausanneCity} is in canton {VaudCanton}, {CountryCodes.Switzerland}");
    }

    [Test]
    public async Task ExecuteAsync_NoHit_ReturnsTheNotFoundError()
    {
        _postcodeRepository.GetByZipAsync(1234).Returns(new List<PostcodeCH>());

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { ["zip"] = 1234 });

        result.Success.ShouldBeFalse();
    }
}
