// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the marketplace entity-import path (IRegionEntityImportService on RegionSetupService):
/// applies industry-profile presets, qualification catalogs, industry-bound compliance rows, macros and
/// the package identity settings from a parsed profile, while never touching the once-only settings
/// sections, their markers, top-level compliance rows or the ACTIVE_INDUSTRIES setting.
/// </summary>

using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Events;
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
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionEntityImportServiceTests
{
    private ISettingsRepository _settingsRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ILanguagePluginService _languagePluginService = null!;
    private IPeriodCapRuleRepository _periodCapRuleRepository = null!;
    private IRestDayRotationRuleRepository _restDayRotationRuleRepository = null!;
    private ICounterRuleRepository _counterRuleRepository = null!;
    private IRestrictedTimeWindowRuleRepository _restrictedTimeWindowRuleRepository = null!;
    private Klacks.Api.Application.Interfaces.IGroupRepository _groupRepository = null!;
    private ISchedulingRuleImportRepository _schedulingRuleImportRepository = null!;
    private ISchedulingRuleRateRevisionImportRepository _schedulingRuleRateRevisionImportRepository = null!;
    private IQualificationImportRepository _qualificationImportRepository = null!;
    private IMacroImportRepository _macroImportRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private Dictionary<string, string> _settingValues = null!;
    private List<(string Type, string Value)> _upsertedSettings = null!;
    private List<PeriodCapRule> _addedPeriodCapRules = null!;
    private List<RestDayRotationRule> _addedRestDayRotationRules = null!;
    private List<CounterRule> _addedCounterRules = null!;
    private List<RestrictedTimeWindowRule> _addedRestrictedTimeWindowRules = null!;
    private List<SchedulingRule> _addedSchedulingRules = null!;
    private List<SchedulingRuleRateRevision> _addedSchedulingRuleRateRevisions = null!;
    private List<Qualification> _addedQualifications = null!;
    private List<Macro> _existingMacros = null!;
    private List<Macro> _addedMacros = null!;
    private List<Macro> _updatedMacros = null!;

    [SetUp]
    public void SetUp()
    {
        _settingValues = new Dictionary<string, string>();
        _upsertedSettings = new List<(string Type, string Value)>();
        _addedPeriodCapRules = new List<PeriodCapRule>();
        _addedRestDayRotationRules = new List<RestDayRotationRule>();
        _addedCounterRules = new List<CounterRule>();
        _addedRestrictedTimeWindowRules = new List<RestrictedTimeWindowRule>();
        _addedSchedulingRules = new List<SchedulingRule>();
        _addedSchedulingRuleRateRevisions = new List<SchedulingRuleRateRevision>();
        _addedQualifications = new List<Qualification>();
        _existingMacros = new List<Macro>();
        _addedMacros = new List<Macro>();
        _updatedMacros = new List<Macro>();

        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository
            .GetSettingNoTracking(Arg.Any<string>())
            .Returns(callInfo =>
            {
                var type = callInfo.Arg<string>();
                return _settingValues.TryGetValue(type, out var value)
                    ? new SettingsModel { Id = Guid.NewGuid(), Type = type, Value = value }
                    : null;
            });
        _settingsRepository
            .UpsertSettingAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                _upsertedSettings.Add((callInfo.ArgAt<string>(0), callInfo.ArgAt<string>(1)));
                return Task.CompletedTask;
            });

        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _languagePluginService = Substitute.For<ILanguagePluginService>();

        _periodCapRuleRepository = Substitute.For<IPeriodCapRuleRepository>();
        _periodCapRuleRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<PeriodCapRule>());
        _periodCapRuleRepository.When(r => r.Add(Arg.Any<PeriodCapRule>()))
            .Do(callInfo => _addedPeriodCapRules.Add(callInfo.Arg<PeriodCapRule>()));

        _restDayRotationRuleRepository = Substitute.For<IRestDayRotationRuleRepository>();
        _restDayRotationRuleRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<RestDayRotationRule>());
        _restDayRotationRuleRepository.When(r => r.Add(Arg.Any<RestDayRotationRule>()))
            .Do(callInfo => _addedRestDayRotationRules.Add(callInfo.Arg<RestDayRotationRule>()));

        _counterRuleRepository = Substitute.For<ICounterRuleRepository>();
        _counterRuleRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<CounterRule>());
        _counterRuleRepository.When(r => r.Add(Arg.Any<CounterRule>()))
            .Do(callInfo => _addedCounterRules.Add(callInfo.Arg<CounterRule>()));

        _restrictedTimeWindowRuleRepository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _restrictedTimeWindowRuleRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<RestrictedTimeWindowRule>());
        _restrictedTimeWindowRuleRepository.When(r => r.Add(Arg.Any<RestrictedTimeWindowRule>()))
            .Do(callInfo => _addedRestrictedTimeWindowRules.Add(callInfo.Arg<RestrictedTimeWindowRule>()));

        _groupRepository = Substitute.For<Klacks.Api.Application.Interfaces.IGroupRepository>();
        _groupRepository.List().Returns(new List<Klacks.Api.Domain.Models.Associations.Group>());

        _schedulingRuleImportRepository = Substitute.For<ISchedulingRuleImportRepository>();
        _schedulingRuleImportRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<SchedulingRule>());
        _schedulingRuleImportRepository.When(r => r.Add(Arg.Any<SchedulingRule>()))
            .Do(callInfo => _addedSchedulingRules.Add(callInfo.Arg<SchedulingRule>()));

        _schedulingRuleRateRevisionImportRepository = Substitute.For<ISchedulingRuleRateRevisionImportRepository>();
        _schedulingRuleRateRevisionImportRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<SchedulingRuleRateRevision>());
        _schedulingRuleRateRevisionImportRepository.When(r => r.Add(Arg.Any<SchedulingRuleRateRevision>()))
            .Do(callInfo => _addedSchedulingRuleRateRevisions.Add(callInfo.Arg<SchedulingRuleRateRevision>()));

        _qualificationImportRepository = Substitute.For<IQualificationImportRepository>();
        _qualificationImportRepository
            .GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<Qualification>());
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
        _macroImportRepository.When(r => r.Update(Arg.Any<Macro>()))
            .Do(callInfo => _updatedMacros.Add(callInfo.Arg<Macro>()));

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(callInfo => callInfo.Arg<Func<Task<int>>>()());

        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();
    }

    private IRegionEntityImportService CreateService()
    {
        var configuration = new ConfigurationBuilder().Build();

        return new RegionSetupService(
            configuration,
            _languagePluginService,
            _settingsRepository,
            _calendarSelectionRepository,
            _periodCapRuleRepository,
            _restDayRotationRuleRepository,
            _counterRuleRepository,
            _restrictedTimeWindowRuleRepository,
            _groupRepository,
            _schedulingRuleImportRepository,
            _schedulingRuleRateRevisionImportRepository,
            _qualificationImportRepository,
            _macroImportRepository,
            new MacroScriptValidator(),
            _unitOfWork,
            _eventDispatcher,
            NullLogger<RegionSetupService>.Instance);
    }

    private static Klacks.Api.Application.DTOs.Setup.RegionSetupProfile ParseProfile(string json)
    {
        return RegionSetupFileReader.Parse(json, "test-profile");
    }

    [Test]
    public async Task ApplyEntityImportsAsync_FullProfile_AppliesOnlyIndustryEntitiesMacrosAndPackage()
    {
        var json = """
            {
              "version": 1,
              "region": "CH",
              "languages": { "install": [], "default": "de" },
              "locale": { "country": "CH", "timeZone": "Europe/Zurich" },
              "calendar": { "weekStartDay": "Monday" },
              "worktime": { "fullTime": 173 },
              "surcharges": { "nightRate": 0.25 },
              "compliance": {
                "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200 } ],
                "restrictedTimeWindows": [
                  { "seasonFrom": "06-15", "seasonTo": "09-15", "dailyStart": "12:30", "dailyEnd": "15:00", "appliesToGroupTag": "outdoor" } ]
              },
              "activeIndustries": [ "healthcare" ],
              "industryProfiles": { "healthcare": {
                "schedulingRulePresets": [ { "name": "CH Klinik Standard", "maxDailyHours": 10 } ],
                "qualificationCatalog": [ { "name": { "de": "Pflegefachkraft", "en": "Registered nurse" }, "isTimeLimited": true } ],
                "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 180 } ]
              } },
              "macros": [ { "name": "CH Zuschlag", "function": "custom", "category": "shift", "content": "import hour\nOUTPUT 1, Hour" } ],
              "package": { "country": "ch", "version": "1.1.0" }
            }
            """;

        await CreateService().ApplyEntityImportsAsync(ParseProfile(json));

        _addedSchedulingRules.Single().Name.ShouldBe("CH Klinik Standard");
        _addedQualifications.Single().Industry.ShouldBe("healthcare");
        _addedMacros.Single().Name.ShouldBe("CH Zuschlag");

        var boundCap = _addedPeriodCapRules.Single();
        boundCap.ImportSourceKey.ShouldStartWith("region-setup:industryProfiles:healthcare:periodCap");
        _addedPeriodCapRules.ShouldNotContain(r => r.ImportSourceKey.StartsWith("region-setup:compliance."));
        _addedRestrictedTimeWindowRules.ShouldBeEmpty();

        _upsertedSettings.Select(s => s.Type).ShouldBe(
            new[] { SettingKeys.RegionPackageCountry, SettingKeys.RegionPackageVersion },
            ignoreOrder: true);
        _upsertedSettings.Single(s => s.Type == SettingKeys.RegionPackageCountry).Value.ShouldBe("ch");
        _upsertedSettings.Single(s => s.Type == SettingKeys.RegionPackageVersion).Value.ShouldBe("1.1.0");

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _languagePluginService.DidNotReceiveWithAnyArgs().InstallAsync(default!);
    }

    [Test]
    public async Task ApplyEntityImportsAsync_IndustryBlockWithRestDayRotationsAndCounterRules_ImportsBoth()
    {
        var json = """
            {
              "version": 1,
              "activeIndustries": [ "healthcare" ],
              "industryProfiles": { "healthcare": {
                "schedulingRulePresets": [ { "name": "CH Klinik Standard", "maxDailyHours": 10 } ],
                "restDayRotations": [ { "dayOfWeek": "sunday", "minFree": 2, "windowWeeks": 4 } ],
                "counterRules": [ { "event": "nightShift", "period": "year", "threshold": 100 } ]
              } },
              "package": { "country": "ch", "version": "1.1.0" }
            }
            """;

        await CreateService().ApplyEntityImportsAsync(ParseProfile(json));

        var rotation = _addedRestDayRotationRules.Single();
        rotation.ImportSourceKey.ShouldStartWith("region-setup:industryProfiles:healthcare:restDayRotation");
        var counter = _addedCounterRules.Single();
        counter.ImportSourceKey.ShouldStartWith("region-setup:industryProfiles:healthcare:counterRule");
    }

    [Test]
    public async Task ApplyEntityImportsAsync_NeverWritesMarkersOrActiveIndustries()
    {
        var json = """
            {
              "version": 1,
              "activeIndustries": [ "healthcare" ],
              "industryProfiles": { "healthcare": {
                "schedulingRulePresets": [ { "name": "Preset A", "maxDailyHours": 9 } ]
              } },
              "package": { "country": "ch", "version": "1.1.0" }
            }
            """;

        await CreateService().ApplyEntityImportsAsync(ParseProfile(json));

        _upsertedSettings.ShouldNotContain(s => s.Type == SettingKeys.ActiveIndustries);
        _upsertedSettings.ShouldNotContain(s => s.Type.StartsWith("REGION_SETUP_APPLIED"));
        _upsertedSettings.ShouldNotContain(s => s.Type == SettingKeys.RegionSetupApplied);
    }

    [Test]
    public async Task ApplyEntityImportsAsync_NothingChanged_SkipsTransactionEntirely()
    {
        _settingValues[SettingKeys.RegionPackageCountry] = "ch";
        _settingValues[SettingKeys.RegionPackageVersion] = "1.1.0";
        var json = """
            { "version": 1, "package": { "country": "ch", "version": "1.1.0" } }
            """;

        await CreateService().ApplyEntityImportsAsync(ParseProfile(json));

        _upsertedSettings.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
    }

    [Test]
    public async Task ApplyEntityImportsAsync_MacroStandardHeldByCustomerMacro_ThrowsWithoutAnyWrite()
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
            { "version": 1,
              "macros": [ { "name": "CH Standard", "function": "standard", "content": "import hour\nOUTPUT 1, Hour" } ],
              "package": { "country": "ch", "version": "1.1.0" } }
            """;
        var service = CreateService();

        await Should.ThrowAsync<InvalidRequestException>(() => service.ApplyEntityImportsAsync(ParseProfile(json)));

        _addedMacros.ShouldBeEmpty();
        _upsertedSettings.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyEntityImportsAsync_CustomerEditedMacro_SkipsWithoutOverwriting()
    {
        var firstJson = """
            { "version": 1, "macros": [ { "name": "CH Zuschlag", "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;
        await CreateService().ApplyEntityImportsAsync(ParseProfile(firstJson));
        var imported = _addedMacros.Single();
        imported.Content = "import hour\nOUTPUT 1, Hour * 2";
        _existingMacros.Add(imported);
        _addedMacros.Clear();

        var secondJson = """
            { "version": 1, "macros": [ { "name": "CH Zuschlag", "content": "import hour\nOUTPUT 1, Hour + 1" } ] }
            """;
        await CreateService().ApplyEntityImportsAsync(ParseProfile(secondJson));

        _addedMacros.ShouldBeEmpty();
        _updatedMacros.ShouldBeEmpty();
        imported.Content.ShouldBe("import hour\nOUTPUT 1, Hour * 2");
    }
}
