// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.CalendarSelections;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// Names the holiday calendar that decides holiday-work warnings and holiday surcharges, in the order the
/// production resolver applies: the contract's own selection, the company default selection, the company
/// country/region pair (only when both are set), otherwise none. The name is what Klacksy may show; ids never are.
/// </summary>
[TestFixture]
public class HolidayCalendarSourceResolverTests
{
    private ISettingsReader _settingsReader = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private Dictionary<string, string> _settings = null!;
    private HolidayCalendarSourceResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _settings = new Dictionary<string, string>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<string, string>)call.Arg<IEnumerable<string>>()
                .Where(_settings.ContainsKey)
                .ToDictionary(k => k, k => _settings[k]));
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _sut = new HolidayCalendarSourceResolver(_settingsReader, _calendarSelectionRepository);
    }

    [Test]
    public async Task ContractSelection_WinsAndIsNamed()
    {
        var id = Guid.NewGuid();
        _calendarSelectionRepository.GetNoTracking(id).Returns(new CalendarSelection { Id = id, Name = "Bern + USA" });
        _settings[SettingKeys.GlobalCalendarSelectionId] = Guid.NewGuid().ToString();

        var resolved = await _sut.ResolveAsync(id);

        resolved.Source.ShouldBe(HolidayCalendarSource.Contract);
        resolved.CalendarSelectionId.ShouldBe(id);
        resolved.CalendarName.ShouldBe("Bern + USA");
    }

    [Test]
    public async Task NoContractSelection_FallsBackToCompanyDefaultSelection()
    {
        var id = Guid.NewGuid();
        _settings[SettingKeys.GlobalCalendarSelectionId] = id.ToString();
        _settings[SettingKeys.GlobalCalendarCountry] = "CH";
        _settings[SettingKeys.GlobalCalendarState] = "BE";
        _calendarSelectionRepository.GetNoTracking(id).Returns(new CalendarSelection { Id = id, Name = "Kanton Bern" });

        var resolved = await _sut.ResolveAsync(null);

        resolved.Source.ShouldBe(HolidayCalendarSource.CompanyDefault);
        resolved.CalendarName.ShouldBe("Kanton Bern");
    }

    [Test]
    public async Task NoSelectionAnywhere_FallsBackToCompanyCountryAndRegion()
    {
        _settings[SettingKeys.GlobalCalendarCountry] = "CH";
        _settings[SettingKeys.GlobalCalendarState] = "BE";

        var resolved = await _sut.ResolveAsync(null);

        resolved.Source.ShouldBe(HolidayCalendarSource.CompanyCountryState);
        resolved.Country.ShouldBe("CH");
        resolved.State.ShouldBe("BE");
        resolved.CalendarSelectionId.ShouldBeNull();
    }

    [TestCase("CH", "")]
    [TestCase("", "BE")]
    [TestCase("", "")]
    public async Task IncompleteCountryAndRegion_MeansNoCalendar(string country, string state)
    {
        _settings[SettingKeys.GlobalCalendarCountry] = country;
        _settings[SettingKeys.GlobalCalendarState] = state;
        _settings[SettingKeys.GlobalCalendarSelectionId] = "not-a-guid";

        var resolved = await _sut.ResolveAsync(null);

        resolved.Source.ShouldBe(HolidayCalendarSource.None);
    }
}
