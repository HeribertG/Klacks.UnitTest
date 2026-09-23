// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the single backend definition of required provider config fields used by the setup diagnosis.
/// </summary>
using Klacks.Plugin.Messaging.Application.Services.Setup;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class ProviderRequiredConfigFieldsTests
{
    [TestCase("{\"BotToken\":\"x\"}", "Telegram", new string[0])]
    [TestCase("{\"BotToken\":\"\"}", "Telegram", new[] { "BotToken" })]
    [TestCase("{}", "WhatsApp", new[] { "AccessToken", "PhoneNumberId", "AppSecret", "VerifyToken" })]
    [TestCase("", "Slack", new[] { "BotToken" })]
    [TestCase("{\"BotToken\":\"x\"}", "Unknown", new string[0])]
    [TestCase("{\"AccessToken\":\"x\"}", "Zalo", new string[0])]
    [TestCase("{}", "Zalo", new[] { "AccessToken" })]
    public void GetMissing_Returns_Empty_Required_Keys(string json, string type, string[] expected)
        => ProviderRequiredConfigFields.GetMissing(type, json).ShouldBe(expected);

    [Test]
    public void GetMissing_Teams_RequiresWebhookUrl()
        => ProviderRequiredConfigFields.GetMissing("MicrosoftTeams", "{}").ShouldBe(new[] { "WebhookUrl" });

    [Test]
    public void GetMissing_Zalo_DoesNotRequireOaId()
        => ProviderRequiredConfigFields.GetMissing("Zalo", "{\"AccessToken\":\"x\",\"OaId\":\"\"}").ShouldBeEmpty();

    [Test]
    public void GetMissing_IsCaseInsensitiveOnConfigKeys()
        => ProviderRequiredConfigFields.GetMissing("Telegram", "{\"bottoken\":\"x\"}").ShouldBeEmpty();

    /// <summary>
    /// Empirically pinned duplicate-key resolution: System.Text.Json's own PropertyNameCaseInsensitive
    /// deserializer resolves a JSON object with the same key under two different casings by taking the
    /// value of the LAST matching property in document order (verified against JsonSerializer.Deserialize
    /// with a matching POCO). GetMissing must mirror that instead of throwing, which is what
    /// JsonNode.Parse with PropertyNameCaseInsensitive did before this fix.
    /// </summary>
    [Test]
    public void GetMissing_DuplicateKeyDifferingOnlyInCase_ResolvesToTheLastOne()
        => ProviderRequiredConfigFields.GetMissing("Telegram", "{\"BotToken\":\"\",\"bottoken\":\"x\"}").ShouldBeEmpty();

    [Test]
    public void GetMissing_DuplicateKeyDifferingOnlyInCase_ReversedOrder_ResolvesToTheLastOne()
        => ProviderRequiredConfigFields.GetMissing("Telegram", "{\"bottoken\":\"x\",\"BotToken\":\"\"}").ShouldBe(new[] { "BotToken" });

    [Test]
    public void GetMissing_DuplicateKeyDifferingOnlyInCase_NeverThrows()
        => Should.NotThrow(() => ProviderRequiredConfigFields.GetMissing("Telegram", "{\"BotToken\":\"x\",\"bottoken\":\"x\"}"));

    [Test]
    public void GetMissing_ExactDuplicateKey_NeverThrowsAndTreatsConfigAsUnparseable()
        => ProviderRequiredConfigFields.GetMissing("Telegram", "{\"BotToken\":\"a\",\"BotToken\":\"b\"}").ShouldBe(new[] { "BotToken" });

    [Test]
    public void GetMissing_MalformedJson_NeverThrows()
        => Should.NotThrow(() => ProviderRequiredConfigFields.GetMissing("Telegram", "{not json"));
}
