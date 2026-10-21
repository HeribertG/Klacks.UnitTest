// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests pinning region-setup behaviour that the main fixture did not cover before the
/// RegionSetupService split: post-commit rate-revision event dispatch, the compensatory-rest backfill,
/// the macro demotion flush order, the try/catch boundary of the fully-applied path, validation order,
/// language-code normalization, deterministic calendar-selection choice and literal content hashes per
/// entity family (independently derived SHA-256 of the pipe-joined field material).
/// </summary>

using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Scheduling;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

public partial class RegionSetupServiceTests
{
    private const string PresetWithRevisionJson = """
        { "version": 1, "industryProfiles": { "security": {
            "schedulingRulePresets": [ { "name": "Sec Std", "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.25 } ] } ] } } }
        """;

    [Test]
    public async Task ApplyAsync_NewRateRevision_DispatchesRateRevisionsImportedEventWithRuleAndValidFrom()
    {
        await CreateService(WriteTempFile(PresetWithRevisionJson)).ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        var dispatched = _dispatchedEvents.OfType<SchedulingRuleRateRevisionsImportedEvent>().Single();
        dispatched.RuleIds.ShouldBe(new[] { rule.Id });
        dispatched.EarliestChangedValidFrom.ShouldBe(new DateOnly(2027, 3, 1));
    }

    [Test]
    public async Task ApplyAsync_NewPresetWithoutRevisions_DoesNotDispatchRateRevisionsImportedEvent()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": { "schedulingRulePresets": [ { "name": "Sec Std", "nightRate": 0.25 } ] } } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedSchedulingRules.Count.ShouldBe(1);
        _dispatchedEvents.OfType<SchedulingRuleRateRevisionsImportedEvent>().ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_UneditedPresetSurchargeFieldChanged_DispatchesRateRevisionsImportedEventWithoutValidFrom()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "security": { "schedulingRulePresets": [ { "name": "Sec Std", "nightRate": 0.25 } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        var inserted = _addedSchedulingRules.Single();
        _existingSchedulingRules.Add(inserted);
        _addedSchedulingRules.Clear();
        _dispatchedEvents.Clear();

        var secondJson = """
            { "version": 1, "industryProfiles": { "security": { "schedulingRulePresets": [ { "name": "Sec Std", "nightRate": 0.30 } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        var dispatched = _dispatchedEvents.OfType<SchedulingRuleRateRevisionsImportedEvent>().Single();
        dispatched.RuleIds.ShouldBe(new[] { inserted.Id });
        dispatched.EarliestChangedValidFrom.ShouldBeNull();
    }

    [Test]
    public async Task ApplyAsync_UneditedPresetNonSurchargeFieldChanged_DoesNotDispatchRateRevisionsImportedEvent()
    {
        var firstJson = """
            { "version": 1, "industryProfiles": { "security": { "schedulingRulePresets": [ { "name": "Sec Std", "maxWeeklyHours": 50 } ] } } }
            """;
        await CreateService(WriteTempFile(firstJson)).ApplyAsync();
        _existingSchedulingRules.Add(_addedSchedulingRules.Single());
        _addedSchedulingRules.Clear();
        _dispatchedEvents.Clear();

        var secondJson = """
            { "version": 1, "industryProfiles": { "security": { "schedulingRulePresets": [ { "name": "Sec Std", "maxWeeklyHours": 45 } ] } } }
            """;
        await CreateService(WriteTempFile(secondJson)).ApplyAsync();

        _dispatchedEvents.OfType<SchedulingRuleRateRevisionsImportedEvent>().ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_IdenticalReImportOfRateRevision_DoesNotDispatchRateRevisionsImportedEvent()
    {
        await CreateService(WriteTempFile(PresetWithRevisionJson)).ApplyAsync();
        _existingSchedulingRules.Add(_addedSchedulingRules.Single());
        _existingSchedulingRuleRateRevisions.Add(_addedSchedulingRuleRateRevisions.Single());
        _addedSchedulingRules.Clear();
        _addedSchedulingRuleRateRevisions.Clear();
        _dispatchedEvents.Clear();

        await CreateService(WriteTempFile(PresetWithRevisionJson)).ApplyAsync();

        _addedSchedulingRuleRateRevisions.ShouldBeEmpty();
        _dispatchedEvents.OfType<SchedulingRuleRateRevisionsImportedEvent>().ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_RateRevisionEventDispatcherThrows_IsSwallowedAndRowsStayImported()
    {
        _eventDispatcher
            .DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("recalculation hook failed"));

        await CreateService(WriteTempFile(PresetWithRevisionJson)).ApplyAsync();

        _addedSchedulingRuleRateRevisions.Count.ShouldBe(1);
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<SchedulingRuleRateRevisionsImportedEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAsync_TransactionLambdaRetried_DispatchesOnlyTheChangesOfTheLastAttempt()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(async callInfo =>
            {
                var operation = callInfo.Arg<Func<Task<int>>>();
                await operation();
                return await operation();
            });

        await CreateService(WriteTempFile(PresetWithRevisionJson)).ApplyAsync();

        var dispatched = _dispatchedEvents.OfType<SchedulingRuleRateRevisionsImportedEvent>().Single();
        dispatched.RuleIds.ShouldBe(new[] { _addedSchedulingRules.Last().Id });
    }

    [Test]
    public async Task ApplyAsync_FullyAppliedInstallationNewRateRevision_DispatchesRateRevisionsImportedEvent()
    {
        StubAllSectionMarkersExist();

        await CreateService(WriteTempFile(PresetWithRevisionJson)).ApplyAsync();

        var rule = _addedSchedulingRules.Single();
        var dispatched = _dispatchedEvents.OfType<SchedulingRuleRateRevisionsImportedEvent>().Single();
        dispatched.RuleIds.ShouldBe(new[] { rule.Id });
        dispatched.EarliestChangedValidFrom.ShouldBe(new DateOnly(2027, 3, 1));
    }

    [Test]
    public async Task ApplyAsync_ComplianceMarkerAlreadyApplied_BackfillsCompensatoryRestFromFile()
    {
        StubSetting(SettingKeys.RegionSetupAppliedCompliance, "true");
        var json = """
            { "version": 1, "compliance": { "compensatoryRest": { "enabled": true, "deadlineDays": 14 } } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        AssertWritten(SettingKeys.ComplianceCompensatoryRestEnabled, "true");
        AssertWritten(SettingKeys.ComplianceCompensatoryRestDeadlineDays, "14");
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.RegionSetupAppliedCompliance);
        _dispatchedEvents.OfType<SurchargeSettingsChangedEvent>().ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_ComplianceMarkerAppliedAndCompensatoryRestAlreadyEnabled_DoesNotBackfill()
    {
        StubSetting(SettingKeys.RegionSetupAppliedCompliance, "true");
        StubSetting(SettingKeys.ComplianceCompensatoryRestEnabled, "false");
        var json = """
            { "version": 1, "compliance": { "compensatoryRest": { "enabled": true, "deadlineDays": 14 } } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.ComplianceCompensatoryRestDeadlineDays);
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.ComplianceCompensatoryRestEnabled);
    }

    [Test]
    public async Task ApplyAsync_ComplianceMarkerAppliedAndCompensatoryRestInvalid_SwallowsBackfillError()
    {
        StubSetting(SettingKeys.RegionSetupAppliedCompliance, "true");
        var json = """
            { "version": 1, "compliance": { "compensatoryRest": { "enabled": true, "deadlineDays": 0 } } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.ComplianceCompensatoryRestEnabled);
        _writtenSettings.ShouldNotContain(setting => setting.Type == SettingKeys.ComplianceCompensatoryRestDeadlineDays);
    }

    [Test]
    public async Task ApplyAsync_MacroDemotion_IsFlushedBeforeTheNewHolderIsInserted()
    {
        _existingMacros.Add(new Macro
        {
            Id = SeededMacroIds.AllShift,
            Name = "AllShift",
            Content = "import hour\nOUTPUT 1, Hour",
            Category = MacroCategoryEnum.Shift,
            Type = (int)MacroFunctionEnum.Standard,
        });
        var callLog = new List<string>();
        _macroImportRepository.When(r => r.Update(Arg.Any<Macro>()))
            .Do(callInfo => callLog.Add($"update:{callInfo.Arg<Macro>().Name}"));
        _macroImportRepository.When(r => r.Add(Arg.Any<Macro>()))
            .Do(callInfo => callLog.Add($"add:{callInfo.Arg<Macro>().Name}"));
        _unitOfWork.When(u => u.CompleteAsync()).Do(_ => callLog.Add("complete"));
        var json = """
            { "version": 1, "macros": [ { "name": "FR Standard", "function": "standard",
                "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        var demoteIndex = callLog.IndexOf("update:AllShift");
        var flushIndex = callLog.IndexOf("complete", demoteIndex + 1);
        var insertIndex = callLog.IndexOf("add:FR Standard");
        demoteIndex.ShouldBeGreaterThanOrEqualTo(0);
        flushIndex.ShouldBeGreaterThan(demoteIndex);
        insertIndex.ShouldBeGreaterThan(flushIndex);
        callLog.Last().ShouldBe("complete");
    }

    [Test]
    public async Task ApplyAsync_FullyAppliedInstallation_ReconcilesTopLevelComplianceRowsAndMacros()
    {
        StubAllSectionMarkersExist();
        var json = """
            { "version": 1,
              "compliance": {
                "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200 } ],
                "restDayRotations": [ { "dayOfWeek": "sunday", "minFree": 2, "windowWeeks": 4 } ],
                "counterRules": [ { "event": "nightShift", "threshold": 25 } ],
                "restrictedTimeWindows": [ { "seasonFrom": "06-15", "seasonTo": "09-15", "dailyStart": "12:30", "dailyEnd": "15:00" } ] },
              "macros": [ { "name": "KR Special", "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedPeriodCapRules.Single().ImportSourceKey.ShouldBe("region-setup:compliance.periodCaps:month:totalhours");
        _addedRestDayRotationRules.Single().ImportSourceKey.ShouldBe("region-setup:compliance.restDayRotations:sunday:4w");
        _addedCounterRules.Single().ImportSourceKey.ShouldBe("region-setup:compliance.counterRules:nightshift:year");
        _addedRestrictedTimeWindowRules.Single().ImportSourceKey.ShouldBe("region-setup:compliance.restrictedTimeWindows:06-15:09-15:1230-1500:*");
        _addedMacros.Single().ImportSourceKey.ShouldBe("region-setup:macros:kr-special");
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_FullyAppliedInstallationWithInvalidMacro_SkipsEntityImportWithoutThrowing()
    {
        StubAllSectionMarkersExist();
        var json = """
            { "version": 1,
              "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200 } ] },
              "macros": [ { "name": "Broken", "content": "OUTPUT 1, NoSuchFunction(1)" } ] }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedPeriodCapRules.ShouldBeEmpty();
        _addedMacros.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync<int>(default!);
    }

    [Test]
    public async Task ApplyAsync_FullyAppliedInstallationWithCustomerHeldMacroFunction_Throws()
    {
        StubAllSectionMarkersExist();
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
            { "version": 1, "macros": [ { "name": "FR Standard", "function": "standard", "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;

        await Should.ThrowAsync<InvalidRequestException>(CreateService(WriteTempFile(json)).ApplyAsync);

        _addedMacros.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyAsync_InvalidIndustryAndInvalidPeriodCap_ReportsTheIndustryErrorFirst()
    {
        var json = """
            { "version": 1,
              "compliance": { "periodCaps": [ { "period": "decade", "scope": "totalHours", "capHours": 1 } ] },
              "industryProfiles": { " ": {} } }
            """;

        var exception = await Should.ThrowAsync<InvalidRequestException>(CreateService(WriteTempFile(json)).ApplyAsync);

        exception.Message.ShouldContain("empty industry key");
    }

    [Test]
    public async Task ApplyAsync_InvalidCounterRuleAndInvalidMacro_ReportsTheCounterRuleErrorFirst()
    {
        var json = """
            { "version": 1,
              "compliance": { "counterRules": [ { "event": "bogus", "threshold": 1 } ] },
              "macros": [ { "name": "Broken", "content": "" } ] }
            """;

        var exception = await Should.ThrowAsync<InvalidRequestException>(CreateService(WriteTempFile(json)).ApplyAsync);

        exception.Message.ShouldContain("compliance.counterRules");
    }

    [Test]
    public async Task ApplyAsync_InvalidPeriodCapAndInvalidTimeZone_ReportsTheEntityErrorBeforeTheSettingsError()
    {
        var json = """
            { "version": 1,
              "locale": { "timeZone": "Mars/Phobos" },
              "compliance": { "periodCaps": [ { "period": "decade", "scope": "totalHours", "capHours": 1 } ] } }
            """;

        var exception = await Should.ThrowAsync<InvalidRequestException>(CreateService(WriteTempFile(json)).ApplyAsync);

        exception.Message.ShouldContain("compliance.periodCaps");
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_MixedCasePluginCode_KeepsManifestCaseForInstallAndDefault()
    {
        _languagePluginService
            .GetPlugin(Arg.Is<string>(code => string.Equals(code, "zh-CN", StringComparison.OrdinalIgnoreCase)))
            .Returns(new LanguagePluginInfo { Code = "zh-CN" });
        _languagePluginService.InstallAsync("zh-CN").Returns(true);
        var json = """
            { "version": 1, "languages": { "install": [ " zh-cn ", "DE" ], "default": "ZH-cn" } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        await _languagePluginService.Received(1).InstallAsync("zh-CN");
        await _languagePluginService.DidNotReceive().InstallAsync("zh-cn");
        await _languagePluginService.DidNotReceive().InstallAsync("de");
        AssertWritten(SettingKeys.DefaultLanguage, "zh-CN");
    }

    [Test]
    public async Task ApplyAsync_LanguagePluginInstallFails_ThrowsWithoutAnyWrite()
    {
        _languagePluginService.GetPlugin("pl").Returns(new LanguagePluginInfo { Code = "pl" });
        _languagePluginService.InstallAsync("pl").Returns(false);
        var json = """
            { "version": 1, "languages": { "install": [ "pl" ] } }
            """;

        await Should.ThrowAsync<InvalidRequestException>(CreateService(WriteTempFile(json)).ApplyAsync);

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task ApplyAsync_CalendarSelectionMultipleMatches_PicksTheLowestIdDeterministically()
    {
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        _calendarSelectionRepository
            .GetIdsByStateAsync("DE", "BY", Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { higherId, lowerId });
        var json = """
            { "version": 1, "locale": { "country": "DE", "calendarSelection": { "country": "DE", "state": "BY" } } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        AssertWritten(SettingKeys.GlobalCalendarSelectionId, lowerId.ToString());
    }

    [Test]
    public async Task ApplyAsync_FixedPeriodCap_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "month", "scope": "totalHours", "capHours": 200, "warnAtPercent": 80 } ] } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedPeriodCapRules.Single().ImportContentHash.ShouldBe("B52898D549B71485AA86355C355B8BFF7FFD415CC8444AE8AE15A1137C722B58");
    }

    [Test]
    public async Task ApplyAsync_CustomWeeksPeriodCap_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "period": "customWeeks", "scope": "totalHours", "capHours": 520, "customPeriodWeeks": 4 } ] } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedPeriodCapRules.Single().ImportContentHash.ShouldBe("90F09F46073D0338D442CC730EB13D26B4A3D833539FBFB67250280BED6426C9");
    }

    [Test]
    public async Task ApplyAsync_RollingAveragePeriodCap_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "compliance": { "periodCaps": [ { "windowWeeks": 17, "maxAverageWeeklyHours": 48 } ] } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedPeriodCapRules.Single().ImportContentHash.ShouldBe("44F02FCC6A82EAF972F7D174B04710253DDE273ADBA227217AA505A914094388");
    }

    [Test]
    public async Task ApplyAsync_RestDayRotation_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "compliance": { "restDayRotations": [ { "dayOfWeek": "sunday", "minFree": 2, "windowWeeks": 4 } ] } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedRestDayRotationRules.Single().ImportContentHash.ShouldBe("7695EA10C264A76DBD4997648281E2A86628CA5C099D0D15C0F7351D9F688A6D");
    }

    [Test]
    public async Task ApplyAsync_CounterRuleWithHoursThreshold_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "compliance": { "counterRules": [ { "event": "shiftExceedingHours", "threshold": 3, "hoursThreshold": 10.5 } ] } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        var added = _addedCounterRules.Single();
        added.ImportSourceKey.ShouldBe("region-setup:compliance.counterRules:shiftexceedinghours:year:10.5h");
        added.ImportContentHash.ShouldBe("E45D50492F5A32EB9C8C65D1E9FB1D1B8BC04CC9B039F3C07356986F5453FB4A");
    }

    [Test]
    public async Task ApplyAsync_RestrictedTimeWindow_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "compliance": { "restrictedTimeWindows": [
                { "seasonFrom": "06-15", "seasonTo": "09-15", "dailyStart": "12:30", "dailyEnd": "15:00", "appliesToGroupTag": "outdoor" } ] } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedRestrictedTimeWindowRules.Single().ImportContentHash.ShouldBe("82991649BEA927E2CC0025FA9D0FC768E42E837E171EC6B781ADA8CAB7F2BE97");
    }

    [Test]
    public async Task ApplyAsync_Macro_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "macros": [ { "name": "KR Special", "content": "import hour\nOUTPUT 1, Hour" } ] }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedMacros.Single().ImportContentHash.ShouldBe("208FDAB1ACBAFB1809391E3BA51C62B444EA3EE01B1B1FE8DAED86AFDCA72CA7");
    }

    [Test]
    public async Task ApplyAsync_QualificationCatalogEntry_StoresTheLiteralContentHash()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": { "qualificationCatalog": [ { "name": { "de": "Sicherheit" } } ] } } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedQualifications.Single().ImportContentHash.ShouldBe("839879008479591FDA9FAF8EF54CF9A44059A423578DF10BD981A69D514E95D9");
    }

    [Test]
    public async Task ApplyAsync_PresetAndRateRevision_StoreTheLiteralContentHashes()
    {
        var json = """
            { "version": 1, "industryProfiles": { "security": {
                "schedulingRulePresets": [ { "name": "Sec Std", "maxWeeklyHours": 50, "rateRevisions": [ { "validFrom": "2027-03-01", "nightRate": 0.25 } ] } ] } } }
            """;

        await CreateService(WriteTempFile(json)).ApplyAsync();

        _addedSchedulingRules.Single().ImportContentHash.ShouldBe("414B3C4C1B7D6091E119FB0DDC3414AC5CECC7ADAA440FF2E0859768C66737AB");
        _addedSchedulingRuleRateRevisions.Single().ImportContentHash.ShouldBe("68D474CCDFCECEBB69C2538D073F24AD09D7BBB2905CE82E313D8B956F5F0FAA");
    }

    private void StubSetting(string type, string value)
    {
        _settingsRepository.GetSetting(type).Returns(new SettingsModel { Id = Guid.NewGuid(), Type = type, Value = value });
    }
}
