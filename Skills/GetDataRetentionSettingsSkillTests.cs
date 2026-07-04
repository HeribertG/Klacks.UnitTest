// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetDataRetentionSettingsSkill, which retrieves the GDPR data retention period via ISettingsRepository.
/// </summary>
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using NSubstitute;
using Shouldly;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetDataRetentionSettingsSkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private GetDataRetentionSettingsSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _skill = new GetDataRetentionSettingsSkill(_settingsRepository);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "testuser",
            UserPermissions = new[] { "CanViewSettings" }
        };
    }

    [Test]
    public async Task ExecuteAsync_NoSettingConfigured_ReturnsDefault()
    {
        _settingsRepository.GetSetting(SettingsConstants.DATA_RETENTION_DAYS)
            .Returns((Settings?)null);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain(SettingsConstants.DATA_RETENTION_DAYS_DEFAULT.ToString());
    }

    [Test]
    public async Task ExecuteAsync_WithConfiguredSetting_ReturnsStoredValue()
    {
        var setting = new Settings
        {
            Id = Guid.NewGuid(),
            Type = SettingsConstants.DATA_RETENTION_DAYS,
            Value = "365"
        };
        _settingsRepository.GetSetting(SettingsConstants.DATA_RETENTION_DAYS).Returns(setting);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("365");
        result.Data.ShouldNotBeNull();
    }

    [Test]
    public async Task ExecuteAsync_WithInvalidStoredValue_FallsBackToDefault()
    {
        var setting = new Settings
        {
            Id = Guid.NewGuid(),
            Type = SettingsConstants.DATA_RETENTION_DAYS,
            Value = "not-a-number"
        };
        _settingsRepository.GetSetting(SettingsConstants.DATA_RETENTION_DAYS).Returns(setting);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain(SettingsConstants.DATA_RETENTION_DAYS_DEFAULT.ToString());
    }
}
