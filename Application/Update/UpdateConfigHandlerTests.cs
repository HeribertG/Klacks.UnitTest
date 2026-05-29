// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Update;

using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Commands.Update;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Handlers.Update;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Update;
using Klacks.Api.Domain.Interfaces.Settings;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class UpdateConfigHandlerTests
{
    [Test]
    public async Task GetConfig_parses_settings_with_defaults()
    {
        var reader = Substitute.For<ISettingsReader>();
        reader.GetSetting(SettingsConstants.UPDATE_AUTO_ENABLED).Returns(new SettingsModel { Type = SettingsConstants.UPDATE_AUTO_ENABLED, Value = "true" });
        reader.GetSetting(SettingsConstants.UPDATE_CHANNEL).Returns(new SettingsModel { Type = SettingsConstants.UPDATE_CHANNEL, Value = "Beta" });
        reader.GetSetting(SettingsConstants.UPDATE_CHECK_INTERVAL_HOURS).Returns(new SettingsModel { Type = SettingsConstants.UPDATE_CHECK_INTERVAL_HOURS, Value = "12" });

        var config = await new GetUpdateConfigQueryHandler(reader).Handle(new GetUpdateConfigQuery(), CancellationToken.None);

        config.AutoEnabled.ShouldBeTrue();
        config.Channel.ShouldBe("Beta");
        config.CheckIntervalHours.ShouldBe(12);
        config.BackupRetentionCount.ShouldBe(3);
    }

    [Test]
    public async Task SaveConfig_adds_missing_and_updates_existing()
    {
        var repo = Substitute.For<ISettingsRepository>();
        repo.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);
        repo.GetSetting(SettingsConstants.UPDATE_CHANNEL).Returns(new SettingsModel { Type = SettingsConstants.UPDATE_CHANNEL, Value = "Stable" });

        var config = new UpdateConfig { AutoEnabled = true, Channel = "Beta", CheckIntervalHours = 8, NotifyOnly = true, BackupRetentionCount = 5 };
        var result = await new SaveUpdateConfigCommandHandler(repo).Handle(new SaveUpdateConfigCommand(config), CancellationToken.None);

        result.Channel.ShouldBe("Beta");
        await repo.Received(1).PutSetting(Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.UPDATE_CHANNEL && s.Value == "Beta"));
        await repo.Received().AddSetting(Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.UPDATE_AUTO_ENABLED && s.Value == "True"));
    }
}
