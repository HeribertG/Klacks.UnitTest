// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for FeaturePluginAssistantSetupHintService: the hint is appended only for a plugin with an
/// assistant setup that is not operational, and the trigger phrase comes from the plugin i18n in the user's
/// language — the full tag first ("zh-TW"), then the base language ("de-CH" to "de"), then English. The
/// translation fake mirrors FeaturePluginService.GetTranslations, which merges English under every
/// requested language, so an unknown regional tag yields the English values.
/// </summary>

using Klacks.Api.Application.DTOs.Plugins;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Application.Services.Plugins;
using Klacks.Api.Domain.Models.Plugins;

namespace Klacks.UnitTest.Application.Services.Plugins;

[TestFixture]
public class FeaturePluginAssistantSetupHintServiceTests
{
    private const string PluginName = "messaging";
    private const string TriggerKey = "messaging.setup-assistant.trigger";
    private const string BaseMessage = "Plugin 'Messaging' installed.";

    private IFeaturePluginService _featurePluginService = null!;
    private FeaturePluginAssistantSetupHintService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _featurePluginService = Substitute.For<IFeaturePluginService>();
        var english = new Dictionary<string, string> { [TriggerKey] = "Check messenger commissioning" };
        _featurePluginService.GetTranslations(Arg.Any<string>()).Returns(english);
        _featurePluginService.GetTranslations("de")
            .Returns(new Dictionary<string, string> { [TriggerKey] = "Messenger-Inbetriebnahme prüfen" });
        _featurePluginService.GetTranslations("zh-TW")
            .Returns(new Dictionary<string, string> { [TriggerKey] = "檢查訊息管道運作狀態" });
        _service = new FeaturePluginAssistantSetupHintService(_featurePluginService);
    }

    private void GivenPlugin(bool withAssistantSetup, bool isOperational)
    {
        _featurePluginService.GetPluginAsync(PluginName).Returns(new FeaturePluginInfo
        {
            Name = PluginName,
            DisplayName = "Messaging",
            IsInstalled = true,
            IsEnabled = true,
            IsOperational = isOperational,
            AssistantSetup = withAssistantSetup
                ? new FeaturePluginAssistantSetup
                {
                    OfferKey = "messaging.setup-assistant.offer",
                    TriggerPhraseKey = TriggerKey,
                    AcceptKey = "messaging.setup-assistant.accept",
                    DeclineKey = "messaging.setup-assistant.decline"
                }
                : null
        });
    }

    [Test]
    public async Task AppendHintAsync_WithAssistantSetupAndNotOperational_AppendsEnglishHint()
    {
        GivenPlugin(withAssistantSetup: true, isOperational: false);

        var message = await _service.AppendHintAsync(PluginName, BaseMessage, "en");

        message.ShouldBe(
            "Plugin 'Messaging' installed. Guided commissioning help is available: tell the user they can say "
            + "\"Check messenger commissioning\" to start it.");
    }

    [Test]
    public async Task AppendHintAsync_WithoutAssistantSetup_ReturnsMessageUnchanged()
    {
        GivenPlugin(withAssistantSetup: false, isOperational: false);

        var message = await _service.AppendHintAsync(PluginName, BaseMessage, "en");

        message.ShouldBe(BaseMessage);
    }

    [Test]
    public async Task AppendHintAsync_PluginAlreadyOperational_ReturnsMessageUnchanged()
    {
        GivenPlugin(withAssistantSetup: true, isOperational: true);

        var message = await _service.AppendHintAsync(PluginName, BaseMessage, "de");

        message.ShouldBe(BaseMessage);
    }

    [Test]
    public async Task AppendHintAsync_RegionalGermanTag_UsesGermanPhrase()
    {
        GivenPlugin(withAssistantSetup: true, isOperational: false);

        var message = await _service.AppendHintAsync(PluginName, BaseMessage, "de-CH");

        message.ShouldContain("\"Messenger-Inbetriebnahme prüfen\"");
        message.ShouldNotContain("Check messenger commissioning");
    }

    [Test]
    public async Task AppendHintAsync_RegionalChineseTag_KeepsFullTag()
    {
        GivenPlugin(withAssistantSetup: true, isOperational: false);

        var message = await _service.AppendHintAsync(PluginName, BaseMessage, "zh-TW");

        message.ShouldContain("\"檢查訊息管道運作狀態\"");
    }

    [Test]
    public async Task AppendHintAsync_NoUserLanguage_FallsBackToEnglish()
    {
        GivenPlugin(withAssistantSetup: true, isOperational: false);

        var message = await _service.AppendHintAsync(PluginName, BaseMessage, null);

        message.ShouldContain("\"Check messenger commissioning\"");
    }

    [Test]
    public async Task AppendHintAsync_UnknownPlugin_ReturnsMessageUnchanged()
    {
        _featurePluginService.GetPluginAsync(PluginName).Returns((FeaturePluginInfo?)null);

        var message = await _service.AppendHintAsync(PluginName, BaseMessage, "en");

        message.ShouldBe(BaseMessage);
    }
}
