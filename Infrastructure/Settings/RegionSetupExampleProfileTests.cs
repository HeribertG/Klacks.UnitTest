// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Parses every shipped example region profile under Klacks.Api/deploy/onprem/regions against the
/// current schema, and — where the testable seam allows it without a database — runs the profile
/// through the real semantic validation of RegionSetupService.ApplyAsync. The setup DTOs carry
/// [JsonUnmappedMemberHandling(Disallow)], so a stale or misspelled field in a shipped example would
/// hard-fail a customer's first boot — the parse test catches that drift at build time. The semantic
/// test additionally catches violations of rules that only surface while building the desired entity
/// rows (e.g. "exactly one schedulingRulePresets entry per industry that also carries periodCaps/
/// restDayRotations/counterRules", periodCaps fixed-period XOR rolling-average, hoursThreshold only
/// allowed with event 'shiftExceedingHours') that a pure JSON parse can never detect, since those DTO
/// fields are all individually optional and therefore always parse successfully.
/// </summary>

using System.Runtime.CompilerServices;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionSetupExampleProfileTests
{
    [Test]
    public async Task ShippedGermanExampleProfile_ParsesAgainstCurrentSchema()
    {
        var path = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "Klacks.Api", "deploy", "onprem", "regions", "de.json"));
        File.Exists(path).ShouldBeTrue($"example profile not found at {path}");

        var profile = await RegionSetupFileReader.ReadProfileAsync(path);

        profile.Version.ShouldBe(1);
        profile.Region.ShouldBe("DE");
        var rollingCap = profile.Compliance.ShouldNotBeNull().PeriodCaps.ShouldNotBeNull().Single();
        rollingCap.WindowWeeks.ShouldBe(24);
        rollingCap.MaxAverageWeeklyHours.ShouldBe(48m);
        profile.Compliance!.Enforcement.ShouldNotBeNull().Rules.ShouldNotBeNull().RollingAverage.ShouldBe("warn");

        var healthcare = profile.IndustryProfiles.ShouldNotBeNull()["healthcare"];
        healthcare.SchedulingRulePresets.ShouldNotBeNull().Single().Name.ShouldBe("DE Klinik Standard");
        healthcare.QualificationCatalog.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Test]
    public async Task ShippedSwedishExampleProfile_CarriesStatutoryOvertimeCaps()
    {
        var profile = await RegionSetupFileReader.ReadProfileAsync(Path.Combine(RegionsDirectory(), "se.json"));

        var caps = profile.Compliance.ShouldNotBeNull().PeriodCaps.ShouldNotBeNull();
        caps.Count.ShouldBe(3);
        caps.Single(c => c.WindowWeeks != null).MaxAverageWeeklyHours.ShouldBe(48m);
        var yearCap = caps.Single(c => c.Period == "year");
        yearCap.Scope.ShouldBe("overtimeHours");
        yearCap.CapHours.ShouldBe(200m);
        var monthCap = caps.Single(c => c.Period == "month");
        monthCap.Scope.ShouldBe("overtimeHours");
        monthCap.CapHours.ShouldBe(50m);
    }

    [Test]
    public async Task ShippedEmiratiExampleProfile_CarriesMiddayRestrictedTimeWindow()
    {
        var profile = await RegionSetupFileReader.ReadProfileAsync(Path.Combine(RegionsDirectory(), "ae.json"));

        profile.Region.ShouldBe("AE");
        var window = profile.Compliance.ShouldNotBeNull().RestrictedTimeWindows.ShouldNotBeNull().Single();
        window.SeasonFrom.ShouldBe("06-15");
        window.SeasonTo.ShouldBe("09-15");
        window.DailyStart.ShouldBe("12:30");
        window.DailyEnd.ShouldBe("15:00");
        window.AppliesToGroupTag.ShouldBe("outdoor");
        profile.Compliance!.Enforcement.ShouldNotBeNull().Rules.ShouldNotBeNull().RestrictedTimeWindow.ShouldBe("block");
    }

    [Test]
    public async Task ShippedNorwegianExampleProfile_CarriesStatutoryCustomWeekOvertimeCaps()
    {
        var profile = await RegionSetupFileReader.ReadProfileAsync(Path.Combine(RegionsDirectory(), "no.json"));

        var caps = profile.Compliance.ShouldNotBeNull().PeriodCaps.ShouldNotBeNull();
        caps.Count.ShouldBe(3);
        caps.ShouldAllBe(c => c.Period == "customWeeks" && c.Scope == "overtimeHours");
        caps.Single(c => c.CustomPeriodWeeks == 1).CapHours.ShouldBe(10m);
        caps.Single(c => c.CustomPeriodWeeks == 4).CapHours.ShouldBe(25m);
        caps.Single(c => c.CustomPeriodWeeks == 52).CapHours.ShouldBe(200m);
    }

    [Test]
    public async Task ShippedDutchExampleProfile_CarriesDualStatutoryRollingAverageCapsAndNoStatutorySurcharges()
    {
        var profile = await RegionSetupFileReader.ReadProfileAsync(Path.Combine(RegionsDirectory(), "nl.json"));

        profile.Region.ShouldBe("NL");
        profile.Languages.ShouldNotBeNull().Install.ShouldNotBeNull().ShouldContain("nl");
        profile.Languages!.Default.ShouldBe("nl");

        var caps = profile.Compliance.ShouldNotBeNull().PeriodCaps.ShouldNotBeNull();
        caps.Count.ShouldBe(2);
        caps.ShouldAllBe(c => c.WindowWeeks != null && c.MaxAverageWeeklyHours != null && c.Scope == null);
        caps.Single(c => c.WindowWeeks == 16).MaxAverageWeeklyHours.ShouldBe(48m);
        caps.Single(c => c.WindowWeeks == 4).MaxAverageWeeklyHours.ShouldBe(55m);

        profile.Worktime.ShouldNotBeNull().MaxDailyHours.ShouldBe(12m);
        profile.Worktime!.MaxWeeklyHours.ShouldBe(60m);

        profile.Surcharges.ShouldNotBeNull().NightWindow.ShouldNotBeNull().Start.ShouldBe("00:00");
        profile.Surcharges!.NightWindow!.End.ShouldBe("06:00");
        profile.Surcharges!.NightRate.ShouldBeNull();
        profile.Surcharges!.HolidayRate.ShouldBeNull();
        profile.Compliance!.RestDayRotations.ShouldBeNull();
        profile.Compliance!.CounterRules.ShouldBeNull();
    }

    [Test]
    public async Task ShippedFinnishExampleProfile_CarriesRollingAverageCapInsteadOfOvertimeCap()
    {
        var profile = await RegionSetupFileReader.ReadProfileAsync(Path.Combine(RegionsDirectory(), "fi.json"));

        var cap = profile.Compliance.ShouldNotBeNull().PeriodCaps.ShouldNotBeNull().Single();
        cap.WindowWeeks.ShouldBe(17);
        cap.MaxAverageWeeklyHours.ShouldBe(48m);
        cap.Scope.ShouldBeNull();
    }

    [Test]
    public async Task ShippedItalianExampleProfile_CarriesCompensatoryRestDeadline()
    {
        var profile = await RegionSetupFileReader.ReadProfileAsync(Path.Combine(RegionsDirectory(), "it.json"));

        var compensatoryRest = profile.Compliance.ShouldNotBeNull().CompensatoryRest.ShouldNotBeNull();
        compensatoryRest.Enabled.ShouldBe(true);
        compensatoryRest.DeadlineDays.ShouldBe(3);
        compensatoryRest.AutoPlan.ShouldBe(false);
        profile.Compliance!.Enforcement.ShouldNotBeNull().Rules.ShouldNotBeNull().CompensatoryRest.ShouldBe("warn");
    }

    [Test]
    public async Task CompensatoryRestWithAutoPlanTrue_ThrowsInvalidRequestException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                tempFile,
                """{ "version": 1, "compliance": { "compensatoryRest": { "enabled": true, "deadlineDays": 14, "autoPlan": true } } }""");

            var service = CreateFreshInstallationService(tempFile);

            await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task CompensatoryRestWithoutDeadlineDays_ThrowsInvalidRequestException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                tempFile,
                """{ "version": 1, "compliance": { "compensatoryRest": { "enabled": true } } }""");

            var service = CreateFreshInstallationService(tempFile);

            await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static IEnumerable<TestCaseData> AllRegionProfilePaths()
    {
        foreach (var path in Directory.GetFiles(RegionsDirectory(), "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return new TestCaseData(path).SetName(Path.GetFileName(path));
        }
    }

    [TestCaseSource(nameof(AllRegionProfilePaths))]
    public async Task ShippedRegionExampleProfile_ParsesAgainstCurrentSchema(string path)
    {
        File.Exists(path).ShouldBeTrue($"example profile not found at {path}");

        var profile = await RegionSetupFileReader.ReadProfileAsync(path);

        profile.Version.ShouldBe(1);
        profile.Region.ShouldNotBeNullOrWhiteSpace();
    }

    // Semantic seam: RegionSetupService.ApplyAsync() is public and, on a completely fresh installation
    // (no marker settings, all repositories mocked and empty), runs the profile all the way through
    // BuildIndustryProfileDesired and every entity-import Build*Desired method WITHOUT touching a real
    // database — every repository call in that path is a pure in-memory read/add against NSubstitute
    // mocks. This exercises the "exactly one preset per industry with bound compliance rules", the
    // periodCaps fixed-vs-rolling exclusivity and the counter-rule hoursThreshold gating for real,
    // which the parse-only test above cannot: those DTO fields are all optional, so a profile that
    // violates a semantic rule still parses cleanly.
    [TestCaseSource(nameof(AllRegionProfilePaths))]
    public async Task ShippedRegionExampleProfile_PassesSemanticStartupValidation(string path)
    {
        var service = CreateFreshInstallationService(path);

        await Should.NotThrowAsync(service.ApplyAsync, $"semantic validation failed for {Path.GetFileName(path)}");
    }

    [Test]
    public async Task ShippedRegionExampleProfile_WithUnknownLanguagePluginCode_ThrowsInvalidRequestException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, """{ "version": 1, "languages": { "install": ["xx"] } }""");

            var service = CreateFreshInstallationService(tempFile);

            await Should.ThrowAsync<InvalidRequestException>(service.ApplyAsync);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static string RegionsDirectory([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
        return Path.Combine(repoRoot, "Klacks.Api", "deploy", "onprem", "regions");
    }

    // Mirrors the real LanguagePluginService discovery: a language plugin exists iff
    // Klacks.Api/Plugins/Languages/<code>/ is a directory. Only real, shipped plugins should be
    // treated as installable here - anything else (e.g. "xx") must still fail validation.
    private static HashSet<string> DiscoveredLanguagePluginCodes([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
        var pluginsDirectory = Path.Combine(repoRoot, "Klacks.Api", "Plugins", "Languages");

        if (!Directory.Exists(pluginsDirectory))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.GetDirectories(pluginsDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static RegionSetupService CreateFreshInstallationService(string filePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RegionSetupService.FileConfigKey] = filePath
            })
            .Build();

        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
        settingsRepository
            .AddSetting(Arg.Any<SettingsModel>())
            .Returns(callInfo => callInfo.Arg<SettingsModel>());

        // Every shipped region file sets locale.calendarSelection, which is resolved against actually
        // seeded CalendarSelection rows on a real installation - unrelated to the industryProfiles
        // semantic rules this test targets. Stubbing a match for every country/state keeps the test
        // focused on BuildIndustryProfileDesired instead of failing on missing seed data.
        var calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        calendarSelectionRepository
            .GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });

        var discoveredPluginCodes = DiscoveredLanguagePluginCodes();
        var languagePluginService = Substitute.For<ILanguagePluginService>();
        languagePluginService.GetPlugin(Arg.Any<string>()).Returns(callInfo =>
        {
            var code = callInfo.Arg<string>();
            return discoveredPluginCodes.Contains(code)
                ? new LanguagePluginInfo { Code = code, Name = code, DisplayName = code }
                : null;
        });
        languagePluginService.InstallAsync(Arg.Any<string>())
            .Returns(callInfo => Task.FromResult(discoveredPluginCodes.Contains(callInfo.Arg<string>())));

        var periodCapRuleRepository = Substitute.For<IPeriodCapRuleRepository>();
        periodCapRuleRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(new List<PeriodCapRule>());

        var restDayRotationRuleRepository = Substitute.For<IRestDayRotationRuleRepository>();
        restDayRotationRuleRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(new List<RestDayRotationRule>());

        var counterRuleRepository = Substitute.For<ICounterRuleRepository>();
        counterRuleRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(new List<CounterRule>());

        var restrictedTimeWindowRuleRepository = Substitute.For<Klacks.Api.Domain.Interfaces.Scheduling.IRestrictedTimeWindowRuleRepository>();
        restrictedTimeWindowRuleRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new List<Klacks.Api.Domain.Models.Scheduling.RestrictedTimeWindowRule>());

        var groupRepository = Substitute.For<Klacks.Api.Application.Interfaces.IGroupRepository>();
        groupRepository.List().Returns(new List<Klacks.Api.Domain.Models.Associations.Group>());

        var schedulingRuleImportRepository = Substitute.For<ISchedulingRuleImportRepository>();
        schedulingRuleImportRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(new List<SchedulingRule>());

        var schedulingRuleRateRevisionImportRepository = Substitute.For<ISchedulingRuleRateRevisionImportRepository>();
        schedulingRuleRateRevisionImportRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(new List<SchedulingRuleRateRevision>());

        var qualificationImportRepository = Substitute.For<IQualificationImportRepository>();
        qualificationImportRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(new List<Qualification>());

        var macroImportRepository = Substitute.For<IMacroImportRepository>();
        macroImportRepository.GetBySourceKeysAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(new List<Macro>());
        macroImportRepository
            .GetActiveFunctionHolderAsync(Arg.Any<MacroCategoryEnum>(), Arg.Any<MacroFunctionEnum>())
            .Returns((Macro?)null);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(callInfo => callInfo.Arg<Func<Task<int>>>()());

        return new RegionSetupService(
            configuration,
            languagePluginService,
            settingsRepository,
            calendarSelectionRepository,
            periodCapRuleRepository,
            restDayRotationRuleRepository,
            counterRuleRepository,
            restrictedTimeWindowRuleRepository,
            groupRepository,
            schedulingRuleImportRepository,
            schedulingRuleRateRevisionImportRepository,
            qualificationImportRepository,
            macroImportRepository,
            new MacroScriptValidator(),
            unitOfWork,
            Substitute.For<Klacks.Api.Domain.Events.IDomainEventDispatcher>(),
            NullLogger<RegionSetupService>.Instance);
    }
}
