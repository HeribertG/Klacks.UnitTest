// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for set_erp_import_schedule: validates the cron expression and time zone before
/// writing, falls back to the currently configured time zone when omitted, and persists the
/// cron expression, time zone and next-run marker together.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetErpImportScheduleSkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SetErpImportScheduleSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new SetErpImportScheduleSkill(_settingsRepository, _unitOfWork);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    [Test]
    public async Task InvalidCron_ReturnsError_NoWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["cronExpression"] = "not a cron"
        });

        result.Success.ShouldBeFalse();
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsModel>());
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<SettingsModel>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task InvalidTimeZone_ReturnsError_NoWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["cronExpression"] = "0 * * * *",
            ["timeZoneId"] = "Not/AZone"
        });

        result.Success.ShouldBeFalse();
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsModel>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ValidScheduleWithExplicitTimeZone_PersistsAllThreeSettings()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["cronExpression"] = "*/15 * * * *",
            ["timeZoneId"] = "Europe/Zurich"
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == ErpImportSettingsTypes.CronExpression && s.Value == "*/15 * * * *"));
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == ErpImportSettingsTypes.CronTimeZoneId && s.Value == "Europe/Zurich"));
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == ErpImportSettingsTypes.NextRunUtc));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task NoTimeZoneGiven_FallsBackToCurrentlyConfiguredTimeZone()
    {
        _settingsRepository.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Vienna" });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["cronExpression"] = "0 8 * * *"
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).PutSetting(
            Arg.Is<SettingsModel>(s => s.Type == ErpImportSettingsTypes.CronTimeZoneId && s.Value == "Europe/Vienna"));
    }
}
