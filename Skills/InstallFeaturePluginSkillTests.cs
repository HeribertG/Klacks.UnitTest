// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for install_feature_plugin: a successful install hands its result message together with the user's language
/// to the assistant-setup hint service and returns what that service produces; the "already installed" path
/// never asks for a hint.
/// </summary>

using Klacks.Api.Application.DTOs.Plugins;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class InstallFeaturePluginSkillTests
{
    private const string PluginName = "messaging";
    private const string NameParameter = "name";
    private const string HintedMessage = "Plugin 'Messaging' installed. Guided commissioning help is available.";

    private IFeaturePluginService _featurePluginService = null!;
    private IFeaturePluginAssistantSetupHintService _hintService = null!;
    private InstallFeaturePluginSkill _skill = null!;

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "admin",
        UserLanguage = "de-CH",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static FeaturePluginInfo Plugin(bool alreadyDone) => new()
    {
        Name = PluginName,
        DisplayName = "Messaging",
        IsInstalled = alreadyDone,
        IsEnabled = alreadyDone
    };

    [SetUp]
    public void SetUp()
    {
        _featurePluginService = Substitute.For<IFeaturePluginService>();
        _featurePluginService.InstallAsync(PluginName).Returns(true);
        _hintService = Substitute.For<IFeaturePluginAssistantSetupHintService>();
        _hintService.AppendHintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(HintedMessage);
        _skill = new InstallFeaturePluginSkill(_featurePluginService, _hintService);
    }

    [Test]
    public async Task ExecuteAsync_Success_ReturnsMessageFromHintServiceForUserLanguage()
    {
        _featurePluginService.GetAllPluginsAsync().Returns(new List<FeaturePluginInfo> { Plugin(false) });

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { [NameParameter] = PluginName });

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(HintedMessage);
        await _hintService.Received(1).AppendHintAsync(PluginName, "Plugin 'Messaging' installed.", "de-CH");
    }

    [Test]
    public async Task ExecuteAsync_AlreadyInstalled_DoesNotAskForHint()
    {
        _featurePluginService.GetAllPluginsAsync().Returns(new List<FeaturePluginInfo> { Plugin(true) });

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object> { [NameParameter] = PluginName });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBe(HintedMessage);
        await _featurePluginService.DidNotReceive().InstallAsync(Arg.Any<string>());
        await _hintService.DidNotReceive().AppendHintAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }
}
