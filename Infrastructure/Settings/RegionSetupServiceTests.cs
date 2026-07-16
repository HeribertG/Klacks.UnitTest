// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the one-time first-run region setup service: no-op without a configured file,
/// skip when the marker setting exists, fail-fast on missing/invalid profiles before any write,
/// and full/partial application of languages, settings, calendar selection and marker hash.
/// </summary>

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Services.Setup;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.Macros;
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
    private IPeriodCapRuleRepository _periodCapRuleRepository = null!;
    private IRestDayRotationRuleRepository _restDayRotationRuleRepository = null!;
    private ICounterRuleRepository _counterRuleRepository = null!;
    private ISchedulingRuleImportRepository _schedulingRuleImportRepository = null!;
    private ISchedulingRuleRateRevisionImportRepository _schedulingRuleRateRevisionImportRepository = null!;
    private IQualificationImportRepository _qualificationImportRepository = null!;
    private IMacroImportRepository _macroImportRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private List<SettingsModel> _writtenSettings = null!;
    private List<PeriodCapRule> _existingPeriodCapRules = null!;
    private List<PeriodCapRule> _addedPeriodCapRules = null!;
    private List<RestDayRotationRule> _existingRestDayRotationRules = null!;
    private List<RestDayRotationRule> _addedRestDayRotationRules = null!;
    private List<CounterRule> _existingCounterRules = null!;
    private List<CounterRule> _addedCounterRules = null!;
    private List<SchedulingRule> _existingSchedulingRules = null!;
    private List<SchedulingRule> _addedSchedulingRules = null!;
    private List<SchedulingRuleRateRevision> _existingSchedulingRuleRateRevisions = null!;
    private List<SchedulingRuleRateRevision> _addedSchedulingRuleRateRevisions = null!;
    private List<Qualification> _existingQualifications = null!;
    private List<Qualification> _addedQualifications = null!;
    private List<Macro> _existingMacros = null!;
    private List<Macro> _addedMacros = null!;
    private List<string> _tempFiles = null!;

    [SetUp]
    public void SetUp()
    {
        _writtenSettings = new List<SettingsModel>();
        _existingPeriodCapRules = new List<PeriodCapRule>();
        _addedPeriodCapRules = new List<PeriodCapRule>();
        _existingRestDayRotationRules = new List<RestDayRotationRule>();
        _addedRestDayRotationRules = new List<RestDayRotationRule>();
        _existingCounterRules = new List<CounterRule>();
        _addedCounterRules = new List<CounterRule>();
        _existingSchedulingRules = new List<SchedulingRule>();
        _addedSchedulingRules = new List<SchedulingRule>();
        _existingSchedulingRuleRateRevisions = new List<SchedulingRuleRateRevision>();
        _addedSchedulingRuleRateRevisions = new List<SchedulingRuleRateRevision>();
        _existingQualifications = new List<Qualification>();
        _addedQualifications = new List<Qualification>();
        _existingMacros = new List<Macro>();
        _addedMacros = new List<Macro>();
        _tempFiles = new List<string>();

        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
        _settingsRepository
            .AddSetting(Arg.Do<SettingsModel>(setting => _writtenSettings.Add(setting)))
            .Returns(callInfo => callInfo.Arg<SettingsModel>());
        _settingsRepository
            .UpsertSettingAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(async callInfo =>
            {
                var type = callInfo.ArgAt<string>(0);
                var value = callInfo.ArgAt<string>(1);
                var existing = await _settingsRepository.GetSetting(type);
                if (existing is not null)
                {
                    existing.Value = value;
                    await _settingsRepository.PutSetting(existing);
                }
                else
                {
                    await _settingsRepository.AddSetting(new SettingsModel { Id = Guid.NewGuid(), Type = type, Value = value });
                }
            });

        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _calendarSelectionRepository
            .GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        _languagePluginService = Substitute.For<ILanguagePluginService>();
        _languagePluginService.GetPlugin(Arg.Any<string>()).Returns((LanguagePluginInfo?)null);

        _periodCapRuleRepository = Substitute.For<IPeriodCapRuleRepository>();
        _periodCapRuleRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(callInfo => _existingPeriodCapRules
                .Where(r => callInfo.Arg<IReadOnlyCollection<string>>().Contains(r.ImportSourceKey))
                .ToList());
        _periodCapRuleRepository.When(r => r.Add(Arg.Any<PeriodCapRule>()))
            .Do(callInfo => _addedPeriodCapRules.Add(callInfo.Arg<PeriodCapRule>()));

        _restDayRotationRuleRepository = Substitute.For<IRestDayRotationRuleRepository>();
        _restDayRotationRuleRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(callInfo => _existingRestDayRotationRules
                .Where(r => callInfo.Arg<IReadOnlyCollection<string>>().Contains(r.ImportSourceKey))
                .ToList());
        _restDayRotationRuleRepository.When(r => r.Add(Arg.Any<RestDayRotationRule>()))
            .Do(callInfo => _addedRestDayRotationRules.Add(callInfo.Arg<RestDayRotationRule>()));

        _counterRuleRepository = Substitute.For<ICounterRuleRepository>();
        _counterRuleRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(callInfo => _existingCounterRules
                .Where(r => callInfo.Arg<IReadOnlyCollection<string>>().Contains(r.ImportSourceKey))
                .ToList());
        _counterRuleRepository.When(r => r.Add(Arg.Any<CounterRule>()))
            .Do(callInfo => _addedCounterRules.Add(callInfo.Arg<CounterRule>()));

        _schedulingRuleImportRepository = Substitute.For<ISchedulingRuleImportRepository>();
        _schedulingRuleImportRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(callInfo => _existingSchedulingRules
                .Where(r => callInfo.Arg<IReadOnlyCollection<string>>().Contains(r.ImportSourceKey))
                .ToList());
        _schedulingRuleImportRepository.When(r => r.Add(Arg.Any<SchedulingRule>()))
            .Do(callInfo => _addedSchedulingRules.Add(callInfo.Arg<SchedulingRule>()));

        _schedulingRuleRateRevisionImportRepository = Substitute.For<ISchedulingRuleRateRevisionImportRepository>();
        _schedulingRuleRateRevisionImportRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(callInfo => _existingSchedulingRuleRateRevisions
                .Where(r => callInfo.Arg<IReadOnlyCollection<string>>().Contains(r.ImportSourceKey))
                .ToList());
        _schedulingRuleRateRevisionImportRepository.When(r => r.Add(Arg.Any<SchedulingRuleRateRevision>()))
            .Do(callInfo => _addedSchedulingRuleRateRevisions.Add(callInfo.Arg<SchedulingRuleRateRevision>()));

        _qualificationImportRepository = Substitute.For<IQualificationImportRepository>();
        _qualificationImportRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(callInfo => _existingQualifications
                .Where(q => callInfo.Arg<IReadOnlyCollection<string>>().Contains(q.ImportSourceKey))
                .ToList());
        _qualificationImportRepository.When(r => r.Add(Arg.Any<Qualification>()))
            .Do(callInfo => _addedQualifications.Add(callInfo.Arg<Qualification>()));

        _macroImportRepository = Substitute.For<IMacroImportRepository>();
        _macroImportRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(callInfo => _existingMacros
                .Where(m => callInfo.Arg<IReadOnlyCollection<string>>().Contains(m.ImportSourceKey))
                .ToList());
        _macroImportRepository
            .GetActiveFunctionHolderAsync(Arg.Any<MacroCategoryEnum>(), Arg.Any<MacroFunctionEnum>())
            .Returns(callInfo => _existingMacros.FirstOrDefault(m =>
                m.Category == callInfo.ArgAt<MacroCategoryEnum>(0) && m.Type == (int)callInfo.ArgAt<MacroFunctionEnum>(1)));
        _macroImportRepository.When(r => r.Add(Arg.Any<Macro>()))
            .Do(callInfo => _addedMacros.Add(callInfo.Arg<Macro>()));

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
    public async Task ApplyAsync_AllSectionMarkersExist_SkipsWithoutReadingFile()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");
        StubAllSectionMarkersExist();
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
        var service = CreateService(WriteTempFile("""{ "version": 1, "regionTypo": "DE" }"""));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);
    }

    [Test]
    public async Task ApplyAsync_InvalidTimeZone_ThrowsWithoutAnyWrite()
    {
        var json = """
            {
              "version": 1,
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
              "version": 1,
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
              "version": 1,
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
              "version": 1,
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
              "export": { "enabledFormats": ["datev", "generic-payroll-csv"], "defaultPayrollTargetSystem": "datev-lug-bewegungsdaten" },
              "compliance": {
                "qualifications": { "expiredMandatoryBlocks": true, "expiryWarningDays": 30 },
                "enforcement": { "defaultMode": "warn", "allowSupervisorOverride": true },
                "rosterPublication": { "minLeadDays": 14, "countWorkdaysOnly": false }
              }
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
        AssertWritten(SettingKeys.QualificationExpiredMandatoryBlocks, "true");
        AssertWritten(SettingKeys.QualificationExpiryWarningDays, "30");
        AssertWritten(SettingKeys.ComplianceEnforcementDefaultMode, "warn");
        AssertWritten(SettingKeys.ComplianceEnforcementAllowSupervisorOverride, "true");
        AssertWritten(SettingKeys.ComplianceRosterPublicationMinLeadDays, "14");
        AssertWritten(SettingKeys.ComplianceRosterPublicationCountWorkdaysOnly, "false");
        AssertWritten(SettingKeys.RegionSetupApplied, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        foreach (var markerKey in AllSectionMarkerKeys)
        {
            AssertWritten(markerKey, "true");
        }
    }

    [Test]
    public async Task ApplyAsync_CalendarSelectionStateNotFound_FallsBackToCountrywideEntry()
    {
        var countrywideId = Guid.NewGuid();
        var json = """
            { "version": 1, "locale": { "calendarSelection": { "country": "DE", "state": "XX" } } }
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
            { "version": 1, "locale": { "calendarSelection": { "country": "ZZ", "state": "" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_OnlyCalendarBlock_WritesOnlyCalendarKeysAndMarker()
    {
        var json = """
            { "version": 1, "calendar": { "weekendDays": "Friday,Saturday", "weekStartDay": "Sunday" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
        await _calendarSelectionRepository.DidNotReceiveWithAnyArgs()
            .GetIdsByStateAsync(default!, default!, Arg.Any<CancellationToken>());

        _writtenSettings.Select(setting => setting.Type).ShouldBe(
            new[] { SettingKeys.WeekendDays, SettingKeys.WeekStartDay, SettingKeys.RegionSetupAppliedCalendar, SettingKeys.RegionSetupApplied },
            ignoreOrder: true);
        AssertWritten(SettingKeys.WeekendDays, "Friday,Saturday");
        AssertWritten(SettingKeys.WeekStartDay, "Sunday");
        AssertWritten(SettingKeys.RegionSetupAppliedCalendar, "true");
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
            { "version": 1, "calendar": { "weekStartDay": "Saturday" } }
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
            { "version": 1, "languages": { "default": "DE" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.DefaultLanguage, "de");
    }

    [Test]
    public async Task ApplyAsync_DefaultLanguageFromInstallList_WritesDefaultLanguageSetting()
    {
        var json = """
            { "version": 1, "languages": { "install": ["pl"], "default": "pl" } }
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
            { "version": 1, "languages": { "default": "xx" }, "calendar": { "weekStartDay": "Monday" } }
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
            { "version": 1, "languages": { "install": [] }, "calendar": { "weekStartDay": "Monday" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.DefaultLanguage);
        AssertWritten(SettingKeys.WeekStartDay, "Monday");
    }

    [Test]
    public async Task ApplyAsync_OnlyGlobalMarkerSet_BackfillsLegacySectionMarkersWithoutRewritingSettings()
    {
        _settingsRepository.GetSetting(SettingKeys.RegionSetupApplied)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.RegionSetupApplied, Value = "OLD-HASH" });
        var json = """
            {
              "version": 1,
              "region": "DE",
              "languages": { "install": ["pl"], "default": "pl" },
              "locale": {
                "country": "DE", "state": "BY", "timeZone": "Europe/Berlin",
                "calendarSelection": { "country": "DE", "state": "BY" }
              },
              "calendar": { "weekendDays": "Saturday,Sunday", "weekStartDay": "Monday" },
              "worktime": { "defaultWorkingHours": 8 },
              "surcharges": { "nightRate": 0.25 },
              "export": { "defaultPayrollTargetSystem": "datev-lug-bewegungsdaten" }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
        await _calendarSelectionRepository.DidNotReceiveWithAnyArgs()
            .GetIdsByStateAsync(default!, default!, Arg.Any<CancellationToken>());

        _writtenSettings.Select(setting => setting.Type).ShouldBe(LegacySectionMarkerKeys, ignoreOrder: true);
        foreach (var markerKey in LegacySectionMarkerKeys)
        {
            AssertWritten(markerKey, "true");
        }

        await _settingsRepository.Received(1).PutSetting(Arg.Is<SettingsModel>(setting => setting.Type == SettingKeys.RegionSetupApplied));
    }

    [Test]
    public async Task ApplyAsync_OneSectionMarkerMissingWithoutGlobalMarker_StillAppliesThatSection()
    {
        // Stand-in for a schema section introduced after this migration (e.g. a future "compliance"
        // block): no such section exists yet, so an existing section (surcharges) is reused here to
        // prove the mechanism — its own marker is missing while every other section is already marked,
        // and no global marker masks it, so it is applied normally rather than skipped or backfilled.
        StubAllSectionMarkersExist();
        _settingsRepository.GetSetting(SettingKeys.RegionSetupAppliedSurcharges).Returns((SettingsModel?)null);
        var json = """
            { "version": 1, "surcharges": { "nightRate": 0.3 } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _writtenSettings.Select(setting => setting.Type).ShouldBe(
            new[] { SettingKeys.NightRate, SettingKeys.RegionSetupAppliedSurcharges, SettingKeys.RegionSetupApplied },
            ignoreOrder: true);
        AssertWritten(SettingKeys.NightRate, "0.3");
        AssertWritten(SettingKeys.RegionSetupAppliedSurcharges, "true");
    }

    [Test]
    public async Task ApplyAsync_NightWindowInSurchargesBlock_WritesNightStartAndNightEndSettings()
    {
        var json = """
            { "version": 1, "surcharges": { "nightRate": 0.25, "nightWindow": { "start": "22:00", "end": "05:00" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.SurchargeNightStart, "22:00");
        AssertWritten(SettingKeys.SurchargeNightEnd, "05:00");
    }

    [Test]
    public async Task ApplyAsync_NightWindowInvalidFormat_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "surcharges": { "nightWindow": { "start": "22h00", "end": "05:00" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_SurchargesMarkerAlreadyApplied_BackfillsNightWindowFromFile()
    {
        _settingsRepository.GetSetting(SettingKeys.RegionSetupAppliedSurcharges)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.RegionSetupAppliedSurcharges, Value = "true" });
        var json = """
            { "version": 1, "surcharges": { "nightWindow": { "start": "22:00", "end": "04:00" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.SurchargeNightStart, "22:00");
        AssertWritten(SettingKeys.SurchargeNightEnd, "04:00");
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.RegionSetupAppliedSurcharges);
    }

    [Test]
    public async Task ApplyAsync_SurchargesMarkerAppliedAndNightStartAlreadySet_DoesNotOverwriteExistingValue()
    {
        _settingsRepository.GetSetting(SettingKeys.RegionSetupAppliedSurcharges)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.RegionSetupAppliedSurcharges, Value = "true" });
        _settingsRepository.GetSetting(SettingKeys.SurchargeNightStart)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.SurchargeNightStart, Value = "21:00" });
        var json = """
            { "version": 1, "surcharges": { "nightWindow": { "start": "22:00", "end": "04:00" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.SurchargeNightEnd, "04:00");
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.SurchargeNightStart);
    }

    [Test]
    public async Task ApplyAsync_SurchargeRateModesAndMinimumsPerHour_WritesConfiguredSettings()
    {
        var json = """
            {
              "version": 1,
              "surcharges": {
                "nightRate": 1.36,
                "rateModes": { "night": "fixedPerHour", "holiday": "fixedPerShift" },
                "minimumsPerHour": { "weekend1": 75.0 }
              }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.SurchargeNightRateMode, "fixedperhour");
        AssertWritten(SettingKeys.SurchargeHolidayRateMode, "fixedpershift");
        AssertWritten(SettingKeys.SurchargeWE1MinimumPerHour, "75.0");
    }

    [Test]
    public async Task ApplyAsync_SurchargeRateModeInvalidValue_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "surcharges": { "rateModes": { "night": "percentage" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_SurchargeRateModeUnknownType_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "surcharges": { "rateModes": { "unknownType": "fixedPerHour" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_OvertimeTiersAndStackingMode_WritesConfiguredSettings()
    {
        var json = """
            {
              "version": 1,
              "surcharges": {
                "stackingMode": "additive",
                "overtime": {
                  "basis": "day",
                  "tiers": [
                    { "afterHours": 10, "rate": 0.75 },
                    { "afterHours": 12, "rate": 1.00 }
                  ]
                }
              }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.SurchargeStackingMode, "additive");
        AssertWritten(SettingKeys.OvertimeBasis, "day");
        AssertWritten(SettingKeys.OvertimeTier1AfterHours, "10");
        AssertWritten(SettingKeys.OvertimeTier1Rate, "0.75");
        AssertWritten(SettingKeys.OvertimeTier2AfterHours, "12");
        AssertWritten(SettingKeys.OvertimeTier2Rate, "1.00");
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.OvertimeTier3AfterHours);
    }

    [Test]
    public async Task ApplyAsync_StackingModeInvalidValue_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "surcharges": { "stackingMode": "sometimes" } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_OvertimeRateModeFixedPerShift_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "surcharges": { "overtime": { "rateMode": "fixedPerShift", "tiers": [ { "afterHours": 10, "rate": 20 } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_OvertimeTiersNotAscending_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "surcharges": { "overtime": { "tiers": [ { "afterHours": 12, "rate": 1.0 }, { "afterHours": 10, "rate": 0.75 } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_MoreThanThreeOvertimeTiers_ThrowsWithoutAnyWrite()
    {
        var json = """
            {
              "version": 1,
              "surcharges": {
                "overtime": {
                  "tiers": [
                    { "afterHours": 8, "rate": 0.25 },
                    { "afterHours": 10, "rate": 0.5 },
                    { "afterHours": 12, "rate": 0.75 },
                    { "afterHours": 14, "rate": 1.0 }
                  ]
                }
              }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_SurchargesMarkerAlreadyApplied_BackfillsStackingModeAndOvertimeTiersFromFile()
    {
        _settingsRepository.GetSetting(SettingKeys.RegionSetupAppliedSurcharges)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.RegionSetupAppliedSurcharges, Value = "true" });
        var json = """
            {
              "version": 1,
              "surcharges": {
                "stackingMode": "additive",
                "overtime": { "basis": "week", "tiers": [ { "afterHours": 45, "rate": 0.25 } ] }
              }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.SurchargeStackingMode, "additive");
        AssertWritten(SettingKeys.OvertimeBasis, "week");
        AssertWritten(SettingKeys.OvertimeTier1AfterHours, "45");
        AssertWritten(SettingKeys.OvertimeTier1Rate, "0.25");
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.RegionSetupAppliedSurcharges);
    }

    [Test]
    public async Task ApplyAsync_SurchargesMarkerAppliedAndOvertimeTierAlreadySet_DoesNotOverwriteExistingValue()
    {
        _settingsRepository.GetSetting(SettingKeys.RegionSetupAppliedSurcharges)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.RegionSetupAppliedSurcharges, Value = "true" });
        _settingsRepository.GetSetting(SettingKeys.OvertimeTier1Rate)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.OvertimeTier1Rate, Value = "0.5" });
        var json = """
            { "version": 1, "surcharges": { "overtime": { "tiers": [ { "afterHours": 10, "rate": 0.75 } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.OvertimeTier1AfterHours);
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.OvertimeTier1Rate);
    }

    [Test]
    public async Task ApplyAsync_SurchargesMarkerAppliedAllOtherSectionsAppliedTooAndFileMissing_DoesNotThrow()
    {
        StubAllSectionMarkersExist();
        var missingPath = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");
        var service = CreateService(missingPath);

        await service.ApplyAsync();

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_ComplianceBlock_WritesQualificationsEnforcementAndRosterPublicationSettings()
    {
        var json = """
            {
              "version": 1,
              "compliance": {
                "qualifications": { "expiredMandatoryBlocks": true, "expiryWarningDays": 30 },
                "enforcement": {
                  "defaultMode": "warn",
                  "rules": { "maxDailyHours": "block", "minRestHours": "Block" },
                  "allowSupervisorOverride": true
                },
                "rosterPublication": { "minLeadDays": 14, "countWorkdaysOnly": true }
              }
            }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.QualificationExpiredMandatoryBlocks, "true");
        AssertWritten(SettingKeys.QualificationExpiryWarningDays, "30");
        AssertWritten(SettingKeys.ComplianceEnforcementDefaultMode, "warn");
        AssertWritten(SettingKeys.ComplianceEnforcementMaxDailyHours, "block");
        AssertWritten(SettingKeys.ComplianceEnforcementMinRestHours, "block");
        AssertWritten(SettingKeys.ComplianceEnforcementAllowSupervisorOverride, "true");
        AssertWritten(SettingKeys.ComplianceRosterPublicationMinLeadDays, "14");
        AssertWritten(SettingKeys.ComplianceRosterPublicationCountWorkdaysOnly, "true");
        AssertWritten(SettingKeys.RegionSetupAppliedCompliance, "true");
    }

    [Test]
    public async Task ApplyAsync_ComplianceEnforcementModeInvalid_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "enforcement": { "defaultMode": "sometimes" } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_ComplianceMarkerAlreadyApplied_SkipsSection_EvenWithoutGlobalMarker()
    {
        StubAllSectionMarkersExist();
        var json = """
            { "version": 1, "compliance": { "qualifications": { "expiredMandatoryBlocks": true } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_LegacyGlobalMarkerPresent_ComplianceSectionStillApplies()
    {
        // Compliance is a brand-new section, deliberately excluded from LegacySections: even an
        // installation carrying only the legacy whole-file REGION_SETUP_APPLIED marker (no per-section
        // markers at all) must still apply it, unlike the six pre-existing sections which backfill/skip.
        _settingsRepository.GetSetting(SettingKeys.RegionSetupApplied)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.RegionSetupApplied, Value = "legacy-hash" });
        var json = """
            { "version": 1, "compliance": { "qualifications": { "expiredMandatoryBlocks": true } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        AssertWritten(SettingKeys.QualificationExpiredMandatoryBlocks, "true");
        AssertWritten(SettingKeys.RegionSetupAppliedCompliance, "true");
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsNewRow_InsertsRuleWithComputedHash()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200, "warnAtPercent": 80 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _addedPeriodCapRules.Count.ShouldBe(1);
        var added = _addedPeriodCapRules[0];
        added.Period.ShouldBe(PeriodCapPeriod.Month);
        added.Scope.ShouldBe(PeriodCapScope.TotalHours);
        added.CapHours.ShouldBe(200m);
        added.WarnAtPercent.ShouldBe(80);
        added.ImportSourceKey.ShouldBe("region-setup:compliance.periodCaps:month:totalhours");
        added.ImportContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsUnchangedSinceLastImport_UpdatesToNewFileValue()
    {
        var firstJson = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200 } ] } }
            """;
        var firstService = CreateService(WriteTempFile(firstJson));
        await firstService.ApplyAsync();
        var inserted = _addedPeriodCapRules.Single();

        // Simulate: this row is still exactly what the first import wrote (no customer edit) - seed
        // the "existing rows" store with the SAME field values and hash the first import recorded.
        _existingPeriodCapRules.Add(new PeriodCapRule
        {
            Id = inserted.Id,
            Period = inserted.Period,
            Scope = inserted.Scope,
            CapHours = inserted.CapHours,
            WarnAtPercent = inserted.WarnAtPercent,
            ImportSourceKey = inserted.ImportSourceKey,
            ImportContentHash = inserted.ImportContentHash,
        });
        _addedPeriodCapRules.Clear();
        var updatedRules = new List<PeriodCapRule>();
        _periodCapRuleRepository.When(r => r.Update(Arg.Any<PeriodCapRule>()))
            .Do(callInfo => updatedRules.Add(callInfo.Arg<PeriodCapRule>()));

        var secondJson = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 250 } ] } }
            """;
        var secondService = CreateService(WriteTempFile(secondJson));

        await secondService.ApplyAsync();

        updatedRules.Count.ShouldBe(1);
        updatedRules[0].CapHours.ShouldBe(250m);
        _addedPeriodCapRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsCustomerEditedSinceLastImport_SkipsWithoutOverwriting()
    {
        var firstJson = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200 } ] } }
            """;
        var firstService = CreateService(WriteTempFile(firstJson));
        await firstService.ApplyAsync();
        var inserted = _addedPeriodCapRules.Single();

        // Simulate a customer edit: the live CapHours changed to 999 but ImportContentHash was never
        // touched (still reflects the original 200 that was imported) - an admin UI edit would behave
        // exactly like this, since it has no reason to recompute the internal import-tracking hash.
        _existingPeriodCapRules.Add(new PeriodCapRule
        {
            Id = inserted.Id,
            Period = inserted.Period,
            Scope = inserted.Scope,
            CapHours = 999m,
            WarnAtPercent = inserted.WarnAtPercent,
            ImportSourceKey = inserted.ImportSourceKey,
            ImportContentHash = inserted.ImportContentHash,
        });
        _addedPeriodCapRules.Clear();
        var updatedRules = new List<PeriodCapRule>();
        _periodCapRuleRepository.When(r => r.Update(Arg.Any<PeriodCapRule>()))
            .Do(callInfo => updatedRules.Add(callInfo.Arg<PeriodCapRule>()));

        var secondJson = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 250 } ] } }
            """;
        var secondService = CreateService(WriteTempFile(secondJson));

        await secondService.ApplyAsync();

        updatedRules.ShouldBeEmpty();
        _addedPeriodCapRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsInvalidPeriod_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "fortnight", "scope": "totalHours", "capHours": 200 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedPeriodCapRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsRollingAverageNewRow_InsertsRuleWithComputedHash()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "windowWeeks": 17, "maxAverageWeeklyHours": 48 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _addedPeriodCapRules.Count.ShouldBe(1);
        var added = _addedPeriodCapRules[0];
        added.RollingWindowWeeks.ShouldBe(17);
        added.MaxAverageWeeklyHours.ShouldBe(48m);
        added.ImportSourceKey.ShouldBe("region-setup:compliance.periodCaps:rolling:17w");
        added.ImportContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsEntryMixesFixedAndRollingFields_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200, "windowWeeks": 17, "maxAverageWeeklyHours": 48 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedPeriodCapRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsEntryHasNeitherFixedNorRollingFields_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "warnAtPercent": 80 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedPeriodCapRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsRollingAverageEntryMissingMaxAverage_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "windowWeeks": 17 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedPeriodCapRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsOvertimeHoursScope_InsertsRuleWithOvertimeScopeAndComputedHash()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "year", "scope": "overtimeHours", "capHours": 200 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _addedPeriodCapRules.Count.ShouldBe(1);
        var added = _addedPeriodCapRules[0];
        added.Period.ShouldBe(PeriodCapPeriod.Year);
        added.Scope.ShouldBe(PeriodCapScope.OvertimeHours);
        added.CapHours.ShouldBe(200m);
        added.CustomPeriodWeeks.ShouldBeNull();
        added.ImportSourceKey.ShouldBe("region-setup:compliance.periodCaps:year:overtimehours");

        // A null customPeriodWeeks must NOT contribute an extra field to the hash material - the hash of
        // a month/quarter/year row has to stay byte-identical to the pre-customWeeks six-field scheme.
        added.ImportContentHash.ShouldBe(ImportContentHasher.ComputeHash(
            PeriodCapPeriod.Year.ToString(),
            PeriodCapScope.OvertimeHours.ToString(),
            200m.ToString("F4", CultureInfo.InvariantCulture),
            string.Empty,
            string.Empty,
            string.Empty));
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsCustomWeeks_InsertsRuleWithCustomPeriodWeeks()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "customWeeks", "customPeriodWeeks": 4, "scope": "overtimeHours", "capHours": 25 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _addedPeriodCapRules.Count.ShouldBe(1);
        var added = _addedPeriodCapRules[0];
        added.Period.ShouldBe(PeriodCapPeriod.CustomWeeks);
        added.Scope.ShouldBe(PeriodCapScope.OvertimeHours);
        added.CapHours.ShouldBe(25m);
        added.CustomPeriodWeeks.ShouldBe(4);
        added.ImportSourceKey.ShouldBe("region-setup:compliance.periodCaps:customweeks4w:overtimehours");

        // For customWeeks rows the window width IS part of the hash material, appended as seventh field.
        added.ImportContentHash.ShouldBe(ImportContentHasher.ComputeHash(
            PeriodCapPeriod.CustomWeeks.ToString(),
            PeriodCapScope.OvertimeHours.ToString(),
            25m.ToString("F4", CultureInfo.InvariantCulture),
            string.Empty,
            string.Empty,
            string.Empty,
            "4"));
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsTwoCustomWeeksWindows_BothImportedWithDistinctSourceKeys()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [
                { "period": "customWeeks", "customPeriodWeeks": 4, "scope": "overtimeHours", "capHours": 25 },
                { "period": "customWeeks", "customPeriodWeeks": 52, "scope": "overtimeHours", "capHours": 200 }
            ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _addedPeriodCapRules.Count.ShouldBe(2);
        _addedPeriodCapRules.Select(r => r.ImportSourceKey).ShouldBe(new[]
        {
            "region-setup:compliance.periodCaps:customweeks4w:overtimehours",
            "region-setup:compliance.periodCaps:customweeks52w:overtimehours",
        });
        _addedPeriodCapRules[0].CustomPeriodWeeks.ShouldBe(4);
        _addedPeriodCapRules[1].CustomPeriodWeeks.ShouldBe(52);
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsCustomWeeksWithoutCustomPeriodWeeks_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "customWeeks", "scope": "overtimeHours", "capHours": 25 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedPeriodCapRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsCustomPeriodWeeksWithMonthPeriod_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "customPeriodWeeks": 4, "scope": "totalHours", "capHours": 200 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedPeriodCapRules.ShouldBeEmpty();
    }

    [TestCase(0)]
    [TestCase(105)]
    public async Task ApplyAsync_PeriodCapsCustomPeriodWeeksOutOfRange_ThrowsWithoutAnyWrite(int weeks)
    {
        var json = $$"""
            { "version": 1, "compliance": { "periodCaps": [ { "period": "customWeeks", "customPeriodWeeks": {{weeks}}, "scope": "overtimeHours", "capHours": 25 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedPeriodCapRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_PeriodCapsRowImportedBeforeCustomWeeksExisted_StillRecognizedAsUneditedAndUpdated()
    {
        // Rows imported before the customWeeks feature were hashed from exactly six fields (no
        // customPeriodWeeks material). Recomputing the live hash of such a row with the extended method
        // must yield the SAME hash - otherwise every pre-existing row would falsely look customer-edited
        // and file updates would be skipped forever. This seeds a row with the literal old-scheme hash.
        var legacyHash = ImportContentHasher.ComputeHash(
            PeriodCapPeriod.Month.ToString(),
            PeriodCapScope.TotalHours.ToString(),
            200m.ToString("F4", CultureInfo.InvariantCulture),
            string.Empty,
            string.Empty,
            string.Empty);
        _existingPeriodCapRules.Add(new PeriodCapRule
        {
            Id = Guid.NewGuid(),
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = 200m,
            WarnAtPercent = null,
            CustomPeriodWeeks = null,
            ImportSourceKey = "region-setup:compliance.periodCaps:month:totalhours",
            ImportContentHash = legacyHash,
        });
        var updatedRules = new List<PeriodCapRule>();
        _periodCapRuleRepository.When(r => r.Update(Arg.Any<PeriodCapRule>()))
            .Do(callInfo => updatedRules.Add(callInfo.Arg<PeriodCapRule>()));

        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 250 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        updatedRules.Count.ShouldBe(1);
        updatedRules[0].CapHours.ShouldBe(250m);
        _addedPeriodCapRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_VersionMissing_ThrowsWithoutAnyWrite()
    {
        var json = """{ "region": "DE" }""";
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_VersionUnsupported_ThrowsWithoutAnyWrite()
    {
        var json = """{ "version": 2, "region": "DE" }""";
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(default!);
    }

    // The six sections that existed before per-section markers were introduced. Used where a test
    // exercises the legacy-global-marker backfill path specifically, which — by design — never
    // touches Compliance (a brand-new section; see RegionSetupService.LegacySections).
    private static readonly string[] LegacySectionMarkerKeys =
    {
        SettingKeys.RegionSetupAppliedLanguages,
        SettingKeys.RegionSetupAppliedLocale,
        SettingKeys.RegionSetupAppliedCalendar,
        SettingKeys.RegionSetupAppliedWorktime,
        SettingKeys.RegionSetupAppliedSurcharges,
        SettingKeys.RegionSetupAppliedExport,
    };

    private static readonly string[] AllSectionMarkerKeys =
    {
        SettingKeys.RegionSetupAppliedLanguages,
        SettingKeys.RegionSetupAppliedLocale,
        SettingKeys.RegionSetupAppliedCalendar,
        SettingKeys.RegionSetupAppliedWorktime,
        SettingKeys.RegionSetupAppliedSurcharges,
        SettingKeys.RegionSetupAppliedExport,
        SettingKeys.RegionSetupAppliedCompliance,
    };

    private void StubAllSectionMarkersExist()
    {
        foreach (var markerKey in AllSectionMarkerKeys)
        {
            _settingsRepository.GetSetting(markerKey)
                .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = markerKey, Value = "true" });
        }
    }

    [Test]
    public async Task ApplyAsync_RestDayRotationNewRow_InsertsRuleWithComputedHash()
    {
        var json = """
            { "version": 1, "compliance": { "restDayRotations": [ { "dayOfWeek": "sunday", "minFree": 2, "windowWeeks": 4 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var added = _addedRestDayRotationRules.Single();
        added.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        added.MinFreeCount.ShouldBe(2);
        added.WindowWeeks.ShouldBe(4);
        added.ImportSourceKey.ShouldBe("region-setup:compliance.restDayRotations:sunday:4w");
        added.ImportContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ApplyAsync_RestDayRotationMinFreeExceedsWindow_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "restDayRotations": [ { "dayOfWeek": "sunday", "minFree": 5, "windowWeeks": 4 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedRestDayRotationRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_RestDayRotationDuplicateDayAndWindow_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "restDayRotations": [
                { "dayOfWeek": "sunday", "minFree": 2, "windowWeeks": 4 },
                { "dayOfWeek": "Sunday", "minFree": 1, "windowWeeks": 4 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedRestDayRotationRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileWithPresetAndQualification_InsertsBothWithImportKeys()
    {
        var json = """
            { "version": 1, "industryProfiles": { "healthcare": {
                "schedulingRulePresets": [ { "name": "DE Klinik Standard", "maxDailyHours": 10, "maxWeeklyHours": 48, "nightStart": "23:00", "nightEnd": "06:00" } ],
                "qualificationCatalog": [ { "name": { "de": "Examinierte Pflegefachkraft", "en": "Registered nurse" }, "isTimeLimited": true } ]
            } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        rule.Name.ShouldBe("DE Klinik Standard");
        rule.MaxDailyHours.ShouldBe(10m);
        rule.MaxWeeklyHours.ShouldBe(48m);
        rule.NightStart.ShouldBe("23:00");
        rule.NightEnd.ShouldBe("06:00");
        rule.ImportSourceKey.ShouldBe("region-setup:industryProfiles:healthcare:rule:de-klinik-standard");
        rule.ImportContentHash.ShouldNotBeNullOrWhiteSpace();

        var qualification = _addedQualifications.Single();
        qualification.Name.De.ShouldBe("Examinierte Pflegefachkraft");
        qualification.Name.En.ShouldBe("Registered nurse");
        qualification.IsTimeLimited.ShouldBeTrue();
        qualification.Category.ShouldBe(QualificationCategory.Healthcare);
        qualification.ImportSourceKey.ShouldBe("region-setup:industryProfiles:healthcare:qualification:examinierte-pflegefachkraft");
        qualification.ImportContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ApplyAsync_IndustryProfilePresetWithRateRevisions_InsertsRevisionsAsChildRowsWithRevKeys()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "NO Vekter Standard", "nightRate": 0.22, "holidayRate": 1.0,
                    "rateRevisions": [
                        { "validFrom": "2027-03-01", "nightRate": 0.27, "holidayRate": 1.0 },
                        { "validFrom": "2028-01-01", "nightRate": 0.30 }
                    ] } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        _addedSchedulingRuleRateRevisions.Count.ShouldBe(2);

        var first = _addedSchedulingRuleRateRevisions[0];
        first.ValidFrom.ShouldBe(new DateOnly(2027, 3, 1));
        first.NightRate.ShouldBe(0.27m);
        first.HolidayRate.ShouldBe(1.0m);
        first.WE1Rate.ShouldBeNull();
        first.SchedulingRuleId.ShouldBe(rule.Id);
        first.ImportSourceKey.ShouldBe("region-setup:industryProfiles:security:rule:no-vekter-standard:rev:2027-03-01");
        first.ImportContentHash.ShouldNotBeNullOrWhiteSpace();

        var second = _addedSchedulingRuleRateRevisions[1];
        second.ValidFrom.ShouldBe(new DateOnly(2028, 1, 1));
        second.NightRate.ShouldBe(0.30m);
        second.HolidayRate.ShouldBeNull();
        second.SchedulingRuleId.ShouldBe(rule.Id);
        second.ImportSourceKey.ShouldBe("region-setup:industryProfiles:security:rule:no-vekter-standard:rev:2028-01-01");
    }

    [Test]
    public async Task ApplyAsync_RateRevisionUneditedRowValueChange_UpdatesRow()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.25 } ] } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var insertedRule = _addedSchedulingRules.Single();
        var insertedRevision = _addedSchedulingRuleRateRevisions.Single();

        _existingSchedulingRules.Add(insertedRule);
        _existingSchedulingRuleRateRevisions.Add(insertedRevision);
        _addedSchedulingRules.Clear();
        _addedSchedulingRuleRateRevisions.Clear();
        var updatedRevisions = new List<SchedulingRuleRateRevision>();
        _schedulingRuleRateRevisionImportRepository.When(r => r.Update(Arg.Any<SchedulingRuleRateRevision>()))
            .Do(callInfo => updatedRevisions.Add(callInfo.Arg<SchedulingRuleRateRevision>()));

        var secondJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.28 } ] } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updatedRevisions.Single().NightRate.ShouldBe(0.28m);
        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_RateRevisionCustomerEdited_SkipsWithoutOverwriting()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.25 } ] } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var insertedRule = _addedSchedulingRules.Single();
        var insertedRevision = _addedSchedulingRuleRateRevisions.Single();

        insertedRevision.NightRate = 0.99m;
        _existingSchedulingRules.Add(insertedRule);
        _existingSchedulingRuleRateRevisions.Add(insertedRevision);
        _addedSchedulingRules.Clear();
        _addedSchedulingRuleRateRevisions.Clear();
        var updatedRevisions = new List<SchedulingRuleRateRevision>();
        _schedulingRuleRateRevisionImportRepository.When(r => r.Update(Arg.Any<SchedulingRuleRateRevision>()))
            .Do(callInfo => updatedRevisions.Add(callInfo.Arg<SchedulingRuleRateRevision>()));

        var secondJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.28 } ] } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updatedRevisions.ShouldBeEmpty();
        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
        insertedRevision.NightRate.ShouldBe(0.99m);
    }

    [Test]
    public async Task ApplyAsync_RateRevisionWithLegacyPhase1Hash_TreatedAsUneditedAndUpgraded()
    {
        // A row imported under Phase 1 carries the six-field legacy digest (before the eight overtime
        // fields joined the hash). The legacy fallback must recognise it as unedited: Update with the
        // full digest, never frozen as customer-edited.
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.25 } ] } ] } } }
            """;
        await CreateService(WriteTempFile(json)).ApplyAsync();
        var insertedRule = _addedSchedulingRules.Single();
        var insertedRevision = _addedSchedulingRuleRateRevisions.Single();

        insertedRevision.ImportContentHash = ComputeLegacyRateRevisionHash(insertedRevision);
        _existingSchedulingRules.Add(insertedRule);
        _existingSchedulingRuleRateRevisions.Add(insertedRevision);
        _addedSchedulingRules.Clear();
        _addedSchedulingRuleRateRevisions.Clear();
        var updatedRevisions = new List<SchedulingRuleRateRevision>();
        _schedulingRuleRateRevisionImportRepository.When(r => r.Update(Arg.Any<SchedulingRuleRateRevision>()))
            .Do(callInfo => updatedRevisions.Add(callInfo.Arg<SchedulingRuleRateRevision>()));

        await CreateService(WriteTempFile(json)).ApplyAsync();

        var updated = updatedRevisions.Single();
        updated.ImportContentHash.ShouldNotBe(ComputeLegacyRateRevisionHash(updated));
        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
    }

    private static string ComputeLegacyRateRevisionHash(SchedulingRuleRateRevision revision) =>
        ImportContentHasher.ComputeHash(
            revision.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FormatLegacyDecimal(revision.NightRate),
            FormatLegacyDecimal(revision.HolidayRate),
            FormatLegacyDecimal(revision.WE1Rate),
            FormatLegacyDecimal(revision.WE2Rate),
            FormatLegacyDecimal(revision.WE3Rate));

    private static string FormatLegacyDecimal(decimal? value) =>
        value?.ToString("F4", CultureInfo.InvariantCulture) ?? string.Empty;

    [Test]
    public async Task ApplyAsync_RateRevisionNewValidFrom_InsertsWithoutTouchingExisting()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.25 } ] } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var insertedRule = _addedSchedulingRules.Single();
        var insertedRevision = _addedSchedulingRuleRateRevisions.Single();

        _existingSchedulingRules.Add(insertedRule);
        _existingSchedulingRuleRateRevisions.Add(insertedRevision);
        _addedSchedulingRules.Clear();
        _addedSchedulingRuleRateRevisions.Clear();
        var updatedRevisions = new List<SchedulingRuleRateRevision>();
        _schedulingRuleRateRevisionImportRepository.When(r => r.Update(Arg.Any<SchedulingRuleRateRevision>()))
            .Do(callInfo => updatedRevisions.Add(callInfo.Arg<SchedulingRuleRateRevision>()));

        var secondJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [
                    { "validFrom": "2027-03-01", "nightRate": 0.25 },
                    { "validFrom": "2028-06-01", "nightRate": 0.31 } ] } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        var inserted = _addedSchedulingRuleRateRevisions.Single();
        inserted.ValidFrom.ShouldBe(new DateOnly(2028, 6, 1));
        inserted.NightRate.ShouldBe(0.31m);
        inserted.SchedulingRuleId.ShouldBe(insertedRule.Id);
    }

    [Test]
    public async Task ApplyAsync_RateRevisionDuplicateValidFrom_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [
                    { "validFrom": "2027-03-01", "nightRate": 0.25 },
                    { "validFrom": "2027-03-01", "nightRate": 0.28 } ] } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
        _addedSchedulingRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_RateRevisionWithoutAnyRate_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01" } ] } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
        _addedSchedulingRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_RateRevisionWithOvertimeOnly_PersistsOvertimeLadderWithoutRates()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [
                    { "validFrom": "2027-03-01", "overtime": { "basis": "week", "rateMode": "fixedPerHour",
                        "tiers": [ { "afterHours": 38, "rate": 0.30 }, { "afterHours": 45, "rate": 0.50 } ] } } ] } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var revision = _addedSchedulingRuleRateRevisions.Single();
        revision.NightRate.ShouldBeNull();
        revision.OvertimeBasis.ShouldBe(OvertimeBasis.Week);
        revision.OvertimeRateMode.ShouldBe(SurchargeRateMode.FixedPerHour);
        revision.OvertimeTier1AfterHours.ShouldBe(38m);
        revision.OvertimeTier1Rate.ShouldBe(0.30m);
        revision.OvertimeTier2AfterHours.ShouldBe(45m);
        revision.OvertimeTier2Rate.ShouldBe(0.50m);
        revision.OvertimeTier3AfterHours.ShouldBeNull();
        revision.ImportContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ApplyAsync_RateRevisionOvertimeTiersNotAscending_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [
                    { "validFrom": "2027-03-01", "overtime": { "tiers": [
                        { "afterHours": 45, "rate": 0.50 }, { "afterHours": 38, "rate": 0.30 } ] } } ] } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
        _addedSchedulingRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_RateRevisionOvertimeRateModeFixedPerShift_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [
                    { "validFrom": "2027-03-01", "overtime": { "rateMode": "fixedPerShift",
                        "tiers": [ { "afterHours": 40, "rate": 0.25 } ] } } ] } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
        _addedSchedulingRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_RateRevisionOvertimeRateChangedOnly_UpdatesRow()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [
                    { "validFrom": "2027-03-01", "overtime": { "tiers": [ { "afterHours": 40, "rate": 0.25 } ] } } ] } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var insertedRule = _addedSchedulingRules.Single();
        var insertedRevision = _addedSchedulingRuleRateRevisions.Single();

        _existingSchedulingRules.Add(insertedRule);
        _existingSchedulingRuleRateRevisions.Add(insertedRevision);
        _addedSchedulingRules.Clear();
        _addedSchedulingRuleRateRevisions.Clear();
        var updatedRevisions = new List<SchedulingRuleRateRevision>();
        _schedulingRuleRateRevisionImportRepository.When(r => r.Update(Arg.Any<SchedulingRuleRateRevision>()))
            .Do(callInfo => updatedRevisions.Add(callInfo.Arg<SchedulingRuleRateRevision>()));

        var secondJson = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [
                    { "validFrom": "2027-03-01", "overtime": { "tiers": [ { "afterHours": 40, "rate": 0.35 } ] } } ] } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updatedRevisions.Single().OvertimeTier1Rate.ShouldBe(0.35m);
        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_RateRevisionOnPresetWithBoundCaps_StillBindsBothToTheSingleRule()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.30 } ] } ],
                "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200 } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        _addedPeriodCapRules.Single().SchedulingRuleId.ShouldBe(rule.Id);
        _addedSchedulingRuleRateRevisions.Single().SchedulingRuleId.ShouldBe(rule.Id);
    }

    [Test]
    public async Task ApplyAsync_IndustryProfilePresetUneditedRowValueChange_UpdatesRow()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 50 } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var inserted = _addedSchedulingRules.Single();

        _existingSchedulingRules.Add(inserted);
        _addedSchedulingRules.Clear();
        var updatedRules = new List<SchedulingRule>();
        _schedulingRuleImportRepository.When(r => r.Update(Arg.Any<SchedulingRule>()))
            .Do(callInfo => updatedRules.Add(callInfo.Arg<SchedulingRule>()));

        var secondJson = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 45 } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updatedRules.Single().MaxWeeklyHours.ShouldBe(45m);
        _addedSchedulingRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_IndustryProfilePresetCustomerEdited_SkipsWithoutOverwriting()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 50 } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var inserted = _addedSchedulingRules.Single();

        // Simulate a customer edit: the live value changed but ImportContentHash still reflects the
        // originally imported values.
        inserted.MaxWeeklyHours = 42m;
        _existingSchedulingRules.Add(inserted);
        _addedSchedulingRules.Clear();
        var updatedRules = new List<SchedulingRule>();
        _schedulingRuleImportRepository.When(r => r.Update(Arg.Any<SchedulingRule>()))
            .Do(callInfo => updatedRules.Add(callInfo.Arg<SchedulingRule>()));

        var secondJson = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 45 } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updatedRules.ShouldBeEmpty();
        _addedSchedulingRules.ShouldBeEmpty();
        inserted.MaxWeeklyHours.ShouldBe(42m);
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileDuplicatePresetName_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard" }, { "name": "ch spitex   STANDARD" } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedSchedulingRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileQualificationWithoutAnyName_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": { "qualificationCatalog": [ { "isTimeLimited": true } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedQualifications.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileQualificationWithNonCoreLanguage_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": { "qualificationCatalog": [ { "name": { "pl": "Licencja" } } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedQualifications.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_FullyAppliedInstallation_StillReconcilesIndustryProfileImports()
    {
        StubAllSectionMarkersExist();
        var json = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 50 } ],
                "qualificationCatalog": [ { "name": { "de": "FaGe" } } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _addedSchedulingRules.Single().Name.ShouldBe("CH Spitex Standard");
        _addedQualifications.Single().Name.De.ShouldBe("FaGe");
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileWithBoundCapAndRotation_BindsRowsToTheBlockRule()
    {
        var json = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 50 } ],
                "periodCaps": [ { "windowWeeks": 17, "maxAverageWeeklyHours": 48 } ],
                "restDayRotations": [ { "dayOfWeek": "sunday", "minFree": 2, "windowWeeks": 4 } ]
            } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        var cap = _addedPeriodCapRules.Single();
        cap.SchedulingRuleId.ShouldBe(rule.Id);
        cap.ImportSourceKey.ShouldBe("region-setup:industryProfiles:spitex:periodCap:rolling:17w");
        var rotation = _addedRestDayRotationRules.Single();
        rotation.SchedulingRuleId.ShouldBe(rule.Id);
        rotation.ImportSourceKey.ShouldBe("region-setup:industryProfiles:spitex:restDayRotation:sunday:4w");
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileBoundCapReapplied_UpdatePathKeepsBinding()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 50 } ],
                "periodCaps": [ { "windowWeeks": 17, "maxAverageWeeklyHours": 48 } ]
            } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var insertedRule = _addedSchedulingRules.Single();
        var insertedCap = _addedPeriodCapRules.Single();

        _existingSchedulingRules.Add(insertedRule);
        _existingPeriodCapRules.Add(insertedCap);
        _addedSchedulingRules.Clear();
        _addedPeriodCapRules.Clear();
        var updatedCaps = new List<PeriodCapRule>();
        _periodCapRuleRepository.When(r => r.Update(Arg.Any<PeriodCapRule>()))
            .Do(callInfo => updatedCaps.Add(callInfo.Arg<PeriodCapRule>()));

        var secondJson = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard", "maxWeeklyHours": 50 } ],
                "periodCaps": [ { "windowWeeks": 17, "maxAverageWeeklyHours": 45 } ]
            } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        var updated = updatedCaps.Single();
        updated.MaxAverageWeeklyHours.ShouldBe(45m);
        updated.SchedulingRuleId.ShouldBe(insertedRule.Id);
        _addedPeriodCapRules.ShouldBeEmpty();
        _addedSchedulingRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_GlobalAndIndustryBoundCapsMixed_GlobalRowStaysUnbound()
    {
        var json = """
            { "version": 1,
              "compliance": { "periodCaps": [ { "windowWeeks": 24, "maxAverageWeeklyHours": 48 } ] },
              "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "CH Spitex Standard" } ],
                "periodCaps": [ { "windowWeeks": 17, "maxAverageWeeklyHours": 48 } ]
            } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        _addedPeriodCapRules.Count.ShouldBe(2);
        _addedPeriodCapRules.Single(c => c.RollingWindowWeeks == 24).SchedulingRuleId.ShouldBeNull();
        _addedPeriodCapRules.Single(c => c.RollingWindowWeeks == 17).SchedulingRuleId.ShouldBe(rule.Id);
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileBoundEntitiesWithTwoPresets_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "spitex": {
                "schedulingRulePresets": [ { "name": "A" }, { "name": "B" } ],
                "periodCaps": [ { "windowWeeks": 17, "maxAverageWeeklyHours": 48 } ]
            } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedSchedulingRules.ShouldBeEmpty();
        _addedPeriodCapRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_IndustryProfilePresetWithOvertimeTiers_WritesRuleOvertimeFields()
    {
        var json = """
            { "version": 1, "industryProfiles": { "hausdienste": {
                "schedulingRulePresets": [ { "name": "AT Hausbetreuung", "overtime": {
                    "basis": "week", "rateMode": "multiplier",
                    "tiers": [ { "afterHours": 40, "rate": 0.25 }, { "afterHours": 48, "rate": 0.5 } ] } } ]
            } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        rule.OvertimeBasis.ShouldBe(OvertimeBasis.Week);
        rule.OvertimeRateMode.ShouldBe(SurchargeRateMode.Multiplier);
        rule.OvertimeTier1AfterHours.ShouldBe(40m);
        rule.OvertimeTier1Rate.ShouldBe(0.25m);
        rule.OvertimeTier2AfterHours.ShouldBe(48m);
        rule.OvertimeTier2Rate.ShouldBe(0.5m);
        rule.OvertimeTier3AfterHours.ShouldBeNull();
    }

    [Test]
    public async Task ApplyAsync_IndustryProfilePresetOvertimeWithoutTiers_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "industryProfiles": { "hausdienste": {
                "schedulingRulePresets": [ { "name": "AT Hausbetreuung", "overtime": { "basis": "week" } } ]
            } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedSchedulingRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileUnknownIndustrySlug_MapsQualificationToOthers()
    {
        var json = """
            { "version": 1, "industryProfiles": { "hausdienste": { "qualificationCatalog": [ { "name": { "de": "Hauswartung" } } ] } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        _addedQualifications.Single().Category.ShouldBe(QualificationCategory.Others);
    }

    [Test]
    public async Task ApplyAsync_CounterRuleNewRow_InsertsRuleWithComputedHash()
    {
        var json = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "period": "year", "threshold": 25 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var added = _addedCounterRules.Single();
        added.EventType.ShouldBe(CounterEventType.NightShift);
        added.Period.ShouldBe(CounterPeriod.Year);
        added.Threshold.ShouldBe(25);
        added.ImportSourceKey.ShouldBe("region-setup:compliance.counterRules:nightshift:year");
        added.ImportContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ApplyAsync_CounterRuleUneditedRowValueChange_UpdatesRow()
    {
        var firstJson = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "period": "year", "threshold": 25 } ] } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var inserted = _addedCounterRules.Single();
        _existingCounterRules.Add(inserted);
        _addedCounterRules.Clear();
        var updated = new List<CounterRule>();
        _counterRuleRepository.When(r => r.Update(Arg.Any<CounterRule>()))
            .Do(callInfo => updated.Add(callInfo.Arg<CounterRule>()));

        var secondJson = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "period": "year", "threshold": 20 } ] } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updated.Single().Threshold.ShouldBe(20);
        _addedCounterRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_CounterRuleUneditedRow_LeavesCustomerEnforcementOverrideUntouched()
    {
        // Enforcement is not part of any of the three import sets (not hashed, not written by the import
        // apply path, not in CounterRuleImportValues). A per-rule override therefore does NOT count as a
        // customer edit (the value fields are unchanged, so the row is "unedited" and re-imported), and
        // the Update path must leave the override in place while it refreshes the value fields.
        var firstJson = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "period": "year", "threshold": 25 } ] } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var inserted = _addedCounterRules.Single();
        inserted.Enforcement = RuleEnforcementMode.Block;
        _existingCounterRules.Add(inserted);
        _addedCounterRules.Clear();
        var updated = new List<CounterRule>();
        _counterRuleRepository.When(r => r.Update(Arg.Any<CounterRule>()))
            .Do(callInfo => updated.Add(callInfo.Arg<CounterRule>()));

        var secondJson = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "period": "year", "threshold": 20 } ] } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        var row = updated.Single();
        row.Threshold.ShouldBe(20);
        row.Enforcement.ShouldBe(RuleEnforcementMode.Block);
        _addedCounterRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_CounterRuleCustomerEdited_SkipsWithoutOverwriting()
    {
        var firstJson = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "period": "year", "threshold": 25 } ] } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var inserted = _addedCounterRules.Single();
        inserted.Threshold = 99;
        _existingCounterRules.Add(inserted);
        _addedCounterRules.Clear();
        var updated = new List<CounterRule>();
        _counterRuleRepository.When(r => r.Update(Arg.Any<CounterRule>()))
            .Do(callInfo => updated.Add(callInfo.Arg<CounterRule>()));

        var secondJson = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "period": "year", "threshold": 20 } ] } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updated.ShouldBeEmpty();
        _addedCounterRules.ShouldBeEmpty();
        inserted.Threshold.ShouldBe(99);
    }

    [Test]
    public async Task ApplyAsync_CounterRuleHoursThresholdWithoutMatchingEvent_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "nightShift", "threshold": 25, "hoursThreshold": 13 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedCounterRules.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_CounterRuleShiftExceedingHoursWithoutHoursThreshold_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "compliance": { "counterRules": [
                { "event": "shiftExceedingHours", "threshold": 3 } ] } }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedCounterRules.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_IndustryProfileWithBoundCounterRule_BindsRowToTheBlockRule()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "CH Security 210h" } ],
                "counterRules": [ { "event": "nightShift", "period": "year", "threshold": 25 } ]
            } } }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        var counter = _addedCounterRules.Single();
        counter.SchedulingRuleId.ShouldBe(rule.Id);
        counter.ImportSourceKey.ShouldBe("region-setup:industryProfiles:security:counterRule:nightshift:year");
    }

    [Test]
    public async Task ApplyAsync_MacrosSectionNewMacro_CompilesAndInsertsWithImportKeys()
    {
        var json = """
            { "version": 1, "macros": [ { "name": "KR Additive Special", "function": "custom", "category": "shift",
                "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        var added = _addedMacros.Single();
        added.Name.ShouldBe("KR Additive Special");
        added.Category.ShouldBe(MacroCategoryEnum.Shift);
        added.Type.ShouldBe((int)MacroFunctionEnum.Custom);
        added.ImportSourceKey.ShouldBe("region-setup:macros:kr-additive-special");
        added.ImportContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ApplyAsync_MacrosSectionContentFailsProbeExecution_ThrowsWithoutAnyWrite()
    {
        var json = """
            { "version": 1, "macros": [ { "name": "Broken", "content": "OUTPUT 1, NoSuchFunction(1)" } ] }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedMacros.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_MacrosSectionParserHangingContent_FailsWithinTimeoutWithoutAnyWrite()
    {
        // "DIM 123abc" empirically loops the SyntaxAnalyser forever - the probe timeout must convert
        // that into a fail-fast import error instead of freezing the application start.
        var json = """
            { "version": 1, "macros": [ { "name": "Hang", "content": "DIM 123abc" } ] }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedMacros.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_MacrosSectionStandardFunction_DemotesSeededHolder()
    {
        _existingMacros.Add(new Macro
        {
            Id = SeededMacroIds.AllShift,
            Name = "AllShift",
            Content = "import hour\nOUTPUT 1, Hour",
            Category = MacroCategoryEnum.Shift,
            Type = (int)MacroFunctionEnum.Standard,
        });
        var updatedMacros = new List<Macro>();
        _macroImportRepository.When(r => r.Update(Arg.Any<Macro>()))
            .Do(callInfo => updatedMacros.Add(callInfo.Arg<Macro>()));

        var json = """
            { "version": 1, "macros": [ { "name": "FR Standard", "function": "standard",
                "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;
        var service = CreateService(WriteTempFile(json));

        await service.ApplyAsync();

        updatedMacros.Single().Type.ShouldBe((int)MacroFunctionEnum.Custom);
        _addedMacros.Single().Type.ShouldBe((int)MacroFunctionEnum.Standard);
    }

    [Test]
    public async Task ApplyAsync_MacrosSectionStandardFunction_DemotesUneditedImportedHolder()
    {
        var firstJson = """
            { "version": 1, "macros": [ { "name": "Old Standard", "function": "standard", "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var oldHolder = _addedMacros.Single();
        _existingMacros.Add(oldHolder);
        _addedMacros.Clear();
        var updatedMacros = new List<Macro>();
        _macroImportRepository.When(r => r.Update(Arg.Any<Macro>()))
            .Do(callInfo => updatedMacros.Add(callInfo.Arg<Macro>()));

        var secondJson = """
            { "version": 1, "macros": [ { "name": "New Standard", "function": "standard", "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        oldHolder.Type.ShouldBe((int)MacroFunctionEnum.Custom);
        updatedMacros.ShouldContain(oldHolder);
        _addedMacros.Single(m => m.Name == "New Standard").Type.ShouldBe((int)MacroFunctionEnum.Standard);
    }

    [Test]
    public async Task ApplyAsync_MacrosSectionStandardFunctionHeldByCustomerMacro_ThrowsWithoutAnyWrite()
    {
        _existingMacros.Add(new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Mein Firmen-Standard",
            Content = "import hour\nOUTPUT 1, Hour",
            Category = MacroCategoryEnum.Shift,
            Type = (int)MacroFunctionEnum.Standard,
            ImportSourceKey = string.Empty,
        });

        var json = """
            { "version": 1, "macros": [ { "name": "FR Standard", "function": "standard",
                "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;
        var service = CreateService(WriteTempFile(json));

        await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);

        _addedMacros.ShouldBeEmpty();
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_MacrosSectionCustomerEditedImport_SkipsWithoutOverwriting()
    {
        var firstJson = """
            { "version": 1, "macros": [ { "name": "KR Additive Special", "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var inserted = _addedMacros.Single();

        inserted.Content = "import hour\nOUTPUT 1, 0";
        _existingMacros.Add(inserted);
        _addedMacros.Clear();
        var updatedMacros = new List<Macro>();
        _macroImportRepository.When(r => r.Update(Arg.Any<Macro>()))
            .Do(callInfo => updatedMacros.Add(callInfo.Arg<Macro>()));

        var secondJson = """
            { "version": 1, "macros": [ { "name": "KR Additive Special", "content": "import hour\nOUTPUT 1, Hour + 1" } ] }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        updatedMacros.ShouldBeEmpty();
        _addedMacros.ShouldBeEmpty();
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
            _periodCapRuleRepository,
            _restDayRotationRuleRepository,
            _counterRuleRepository,
            _schedulingRuleImportRepository,
            _schedulingRuleRateRevisionImportRepository,
            _qualificationImportRepository,
            _macroImportRepository,
            new MacroScriptValidator(),
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
