// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the one-time first-run region setup service: no-op without a configured file,
/// skip when the marker setting exists, fail-fast on missing/invalid profiles before any write,
/// and full/partial application of languages, settings, calendar selection and marker hash.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionSetupServiceTests
{
    private ISettingsRepository _settingsRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ILanguagePluginService _languagePluginService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private List<SettingsModel> _writtenSettings = null!;
    private List<string> _tempFiles = null!;

    [SetUp]
    public void SetUp()
    {
        _writtenSettings = new List<SettingsModel>();
        _tempFiles = new List<string>();

        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
        _settingsRepository
            .AddSetting(Arg.Do<SettingsModel>(setting => _writtenSettings.Add(setting)))
            .Returns(callInfo => callInfo.Arg<SettingsModel>());

        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _calendarSelectionRepository
            .GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        _languagePluginService = Substitute.For<ILanguagePluginService>();
        _languagePluginService.GetPlugin(Arg.Any<string>()).Returns((LanguagePluginInfo?)null);

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(callInfo => callInfo.Arg<Func<Task<int>>>()());
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task ApplyAsync_NoFileConfigured_DoesNothing()
    {
        var service = CreateService(null);

        await service.ApplyAsync();

        await _settingsRepository.DidNotReceiveWithAnyArgs().GetSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
    }

    [Test]
    public async Task ApplyAsync_MarkerExists_SkipsWithoutReadingFile()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");
        _settingsRepository.GetSetting(SettingKeys.RegionSetupApplied)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.RegionSetupApplied, Value = "ABC" });
        var service = CreateService(missingPath);

        await service.ApplyAsync();

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
    }

    [Test]
    public async Task ApplyAsync_ConfiguredFileMissing_Throws()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");
        var service = CreateService(missingPath);

        await Should.ThrowAsync<FileNotFoundException>(service.ApplyAsync);
    }

    [Test]
    public async Task ApplyAsync_InvalidJson_Throws()
    {
        var service = CreateService(WriteTempFile("{ this is not json"));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);
    }

    [Test]
    public async Task ApplyAsync_UnknownProperty_Throws()
    {
        var service = CreateService(WriteTempFile("""{ "regionTypo": "DE" }"""));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);
    }

    [Test]
    public async Task ApplyAsync_InvalidTimeZone_ThrowsWithoutAnyWrite()
    {
        var json = """
            {
              "locale": { "country": "DE", "timeZone": "Mars/Olympus" },
              "worktime": { "fullTime": 173 }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
    }

    [Test]
    public async Task ApplyAsync_InvalidWeekendDay_ThrowsWithoutAnyWrite()
    {
        var json = """
            {
              "calendar": { "weekendDays": "Saturday,Funday" },
              "surcharges": { "nightRate": 0.25 }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_UnknownLanguageCode_ThrowsWithoutInstallOrWrite()
    {
        var json = """
            {
              "languages": { "install": ["xx"] },
              "calendar": { "weekStartDay": "Monday" }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_HappyPath_InstallsLanguagesWritesSettingsAndMarker()
    {
        var calendarSelectionId = Guid.NewGuid();
        var json = """
            {
              "region": "DE",
              "languages": { "install": ["pl", "de"] },
              "locale": {
                "country": "DE", "state": "BY", "timeZone": "Europe/Berlin",
                "calendarSelection": { "country": "DE", "state": "BY" }
              },
              "calendar": { "weekendDays": "Saturday,Sunday", "weekStartDay": "Monday" },
              "worktime": {
                "maximumHours": 200, "minimumHours": 160, "fullTime": 173,
                "guaranteedHours": 170, "defaultWorkingHours": 8, "overtimeThreshold": 40,
                "vacationDaysPerYear": 24,
                "maxDailyHours": 10, "maxWeeklyHours": 48, "maxConsecutiveDays": 6,
                "minRestDays": 1, "minPauseHours": 11.5
              },
              "surcharges": { "nightRate": 0.25, "holidayRate": 1.0, "we1Rate": 0.5, "we2Rate": 0.5, "we3Rate": 0 },
              "export": { "enabledFormats": ["datev", "generic-payroll-csv"], "defaultPayrollTargetSystem": "datev-lug-bewegungsdaten" }
            }
            """;
        _languagePluginService.GetPlugin("pl").Returns(new LanguagePluginInfo { Code = "pl" });
        _languagePluginService.InstallAsync("pl").Returns(true);
        _calendarSelectionRepository
            .GetIdsByStateAsync("DE", "BY", Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { calendarSelectionId });
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        await _languagePluginService.Received(1).InstallAsync("pl");
        await _languagePluginService.DidNotReceive().InstallAsync("de");

        AssertWritten(SettingsConstants.APP_ADDRESS_COUNTRY, "DE");
        AssertWritten(SettingKeys.GlobalCalendarCountry, "DE");
        AssertWritten(SettingsConstants.APP_ADDRESS_STATE, "BY");
        AssertWritten(SettingKeys.GlobalCalendarState, "BY");
        AssertWritten(SettingsConstants.APP_ADDRESS_TIMEZONE, "Europe/Berlin");
        AssertWritten(SettingKeys.WeekendDays, "Saturday,Sunday");
        AssertWritten(SettingKeys.WeekStartDay, "Monday");
        AssertWritten(SettingKeys.MaximumHours, "200");
        AssertWritten(SettingKeys.MinimumHours, "160");
        AssertWritten(SettingKeys.FullTime, "173");
        AssertWritten(SettingKeys.GuaranteedHours, "170");
        AssertWritten(SettingKeys.DefaultWorkingHours, "8");
        AssertWritten(SettingKeys.OvertimeThreshold, "40");
        AssertWritten(SettingKeys.VacationDaysPerYear, "24");
        AssertWritten(SettingKeys.SchedulingMaxDailyHours, "10");
        AssertWritten(SettingKeys.SchedulingMaxWeeklyHours, "48");
        AssertWritten(SettingKeys.SchedulingMaxConsecutiveDays, "6");
        AssertWritten(SettingKeys.SchedulingMinRestDays, "1");
        AssertWritten(SettingKeys.SchedulingMinPauseHours, "11.5");
        AssertWritten(SettingKeys.NightRate, "0.25");
        AssertWritten(SettingKeys.HolidayRate, "1.0");
        AssertWritten(SettingKeys.WE1Rate, "0.5");
        AssertWritten(SettingKeys.WE2Rate, "0.5");
        AssertWritten(SettingKeys.WE3Rate, "0");
        AssertWritten(SettingKeys.EnabledExportFormats, "datev,generic-payroll-csv");
        AssertWritten(SettingKeys.DefaultPayrollTargetSystem, "datev-lug-bewegungsdaten");
        AssertWritten(SettingKeys.GlobalCalendarSelectionId, calendarSelectionId.ToString());
        AssertWritten(SettingKeys.RegionSetupApplied, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    [Test]
    public async Task ApplyAsync_CalendarSelectionStateNotFound_FallsBackToCountrywideEntry()
    {
        var countrywideId = Guid.NewGuid();
        var json = """
            { "locale": { "calendarSelection": { "country": "DE", "state": "XX" } } }
            """;
        _calendarSelectionRepository
            .GetIdsByStateAsync("DE", "XX", Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        _calendarSelectionRepository
            .GetIdsByStateAsync("DE", "DE", Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { countrywideId });
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.GlobalCalendarSelectionId, countrywideId.ToString());
    }

    [Test]
    public async Task ApplyAsync_CalendarSelectionUnresolvable_Throws()
    {
        var json = """
            { "locale": { "calendarSelection": { "country": "ZZ", "state": "" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_OnlyCalendarBlock_WritesOnlyCalendarKeysAndMarker()
    {
        var json = """
            { "calendar": { "weekendDays": "Friday,Saturday", "weekStartDay": "Sunday" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
        await _calendarSelectionRepository.DidNotReceiveWithAnyArgs()
            .GetIdsByStateAsync(default!, default!, Arg.Any<CancellationToken>());

        _writtenSettings.Select(setting => setting.Type).ShouldBe(
            new[] { SettingKeys.WeekendDays, SettingKeys.WeekStartDay, SettingKeys.RegionSetupApplied },
            ignoreOrder: true);
        AssertWritten(SettingKeys.WeekendDays, "Friday,Saturday");
        AssertWritten(SettingKeys.WeekStartDay, "Sunday");
    }

    [Test]
    public async Task ApplyAsync_ExistingSettingValue_IsOverwritten()
    {
        var existing = new SettingsModel
        {
            Id = Guid.NewGuid(),
            Type = SettingKeys.WeekStartDay,
            Value = "Monday"
        };
        _settingsRepository.GetSetting(SettingKeys.WeekStartDay).Returns(existing);
        var json = """
            { "calendar": { "weekStartDay": "Saturday" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        existing.Value.ShouldBe("Saturday");
        await _settingsRepository.Received(1).PutSetting(existing);
    }

    [Test]
    public async Task ApplyAsync_DefaultLanguageCoreLanguage_WritesDefaultLanguageSetting()
    {
        var json = """
            { "languages": { "default": "DE" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.DefaultLanguage, "de");
    }

    [Test]
    public async Task ApplyAsync_DefaultLanguageFromInstallList_WritesDefaultLanguageSetting()
    {
        var json = """
            { "languages": { "install": ["pl"], "default": "pl" } }
            """;
        _languagePluginService.GetPlugin("pl").Returns(new LanguagePluginInfo { Code = "pl" });
        _languagePluginService.InstallAsync("pl").Returns(true);
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        await _languagePluginService.Received(1).InstallAsync("pl");
        AssertWritten(SettingKeys.DefaultLanguage, "pl");
    }

    [Test]
    public async Task ApplyAsync_DefaultLanguageUnknown_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "languages": { "default": "xx" }, "calendar": { "weekStartDay": "Monday" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
    }

    [Test]
    public async Task ApplyAsync_NoDefaultLanguage_DoesNotWriteDefaultLanguageSetting()
    {
        var json = """
            { "languages": { "install": [] }, "calendar": { "weekStartDay": "Monday" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.DefaultLanguage);
        AssertWritten(SettingKeys.WeekStartDay, "Monday");
    }

    private RegionSetupService CreateService(string? filePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RegionSetupService.FileConfigKey] = filePath
            })
            .Build();

        return new RegionSetupService(
            configuration,
            _languagePluginService,
            _settingsRepository,
            _calendarSelectionRepository,
            _unitOfWork,
            NullLogger<RegionSetupService>.Instance);
    }

    private string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private void AssertWritten(string type, string expectedValue)
    {
        var setting = _writtenSettings.SingleOrDefault(written => written.Type == type);
        setting.ShouldNotBeNull($"setting '{type}' was not written");
        setting!.Value.ShouldBe(expectedValue, $"setting '{type}' has an unexpected value");
    }
}
