// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ErpImportCronTimeZone.ResolveAsync: an explicitly configured ERP_IMPORT_CRON_TIMEZONE
/// setting wins, otherwise the company's own configured time zone is used - never a hard-coded regional
/// default. A configured zone that differs from the company zone is honoured but warned about, with
/// both ids in the log, so a row left over from the old always-persist behaviour stops being invisible
/// on a non-Swiss installation - but only once per distinct pair of zones, because the runner resolves
/// the zone on every due check, once a minute.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpImportCronTimeZoneTests
{
    [Test]
    public async Task ResolveAsync_ExplicitSettingConfigured_UsesIt()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Vienna" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("Europe/Vienna");
    }

    [Test]
    public async Task ResolveAsync_NoSettingConfigured_FallsBackToTheCompanyZone()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId).Returns((SettingsModel?)null);
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("Asia/Kolkata");
    }

    [Test]
    public async Task ResolveAsync_ExplicitWindowsSettingConfigured_ReturnsTheIanaId()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "W. Europe Standard Time" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("Europe/Berlin");
    }

    [Test]
    public async Task ResolveAsync_NoSettingConfigured_WindowsCompanyZone_ReturnsTheIanaId()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId).Returns((SettingsModel?)null);
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("Europe/Berlin");
    }

    [Test]
    public async Task ResolveAsync_UnresolvableSettingConfigured_FallsBackToTheCompanyZone()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Not/AZone" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("Asia/Kolkata");
    }

    [Test]
    public async Task ResolveAsync_UnresolvableSettingConfigured_LogsWarning()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Not/AZone" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var logger = Substitute.For<ILogger>();

        await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, new ErpCronTimeZoneDriftNotifier());

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResolveAsync_ConfiguredZoneDiffersFromTheCompanyZone_LogsWarningWithBothIds()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Zurich" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var logger = Substitute.For<ILogger>();

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe(
            "Europe/Zurich",
            "the configured setting still wins - a Swiss customer legitimately imports on Swiss time, " +
            "so this is a warning and never a silent data migration");
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("Europe/Zurich") && state.ToString()!.Contains("Asia/Kolkata")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResolveAsync_ConfiguredZoneEqualsTheCompanyZone_LogsNothing()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Asia/Kolkata" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var logger = Substitute.For<ILogger>();

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("Asia/Kolkata");
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResolveAsync_ConfiguredWindowsZoneNamingTheCompanyZone_IsNotReportedAsDrift()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "W. Europe Standard Time" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        var logger = Substitute.For<ILogger>();

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("Europe/Berlin");
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResolveAsync_BlankSettingConfigured_FallsBackToTheCompanyZone()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "  " });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance, new ErpCronTimeZoneDriftNotifier());

        result.ShouldBe("UTC");
    }

    [Test]
    public async Task ResolveAsync_SameDriftingPairResolvedTwice_WarnsOnlyOnce()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Zurich" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var logger = Substitute.For<ILogger>();
        var driftNotifier = new ErpCronTimeZoneDriftNotifier();

        await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, driftNotifier);
        await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, driftNotifier);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResolveAsync_DriftingPairChanges_WarnsAgain()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Zurich" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var logger = Substitute.For<ILogger>();
        var driftNotifier = new ErpCronTimeZoneDriftNotifier();

        await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, driftNotifier);

        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Vienna" });
        await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger, driftNotifier);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("Europe/Zurich")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("Europe/Vienna")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
