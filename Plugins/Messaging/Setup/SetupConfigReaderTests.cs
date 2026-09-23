// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SetupConfigReader and SetupSecretRedactor: case-insensitive reads, duplicate and malformed
/// config never throw, and only the per-type credential values (the Teams webhook URL included) are redacted.
/// </summary>
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Services.Setup;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class SetupConfigReaderTests
{
    [Test]
    public void GetString_MatchesKeyCaseInsensitively()
    {
        SetupConfigReader.GetString("{\"webhookurl\":\" https://a.example.com \"}", SetupConfigKeys.WebhookUrl)
            .ShouldBe("https://a.example.com");
    }

    [TestCase("{\"BotToken\":\"first\",\"BotToken\":\"second\"}")]
    [TestCase("{\"BotToken\":\"first\",\"bottoken\":\"second\"}")]
    public void GetString_DuplicateKeys_NeverThrowsAndLastWins(string configJson)
    {
        SetupConfigReader.GetString(configJson, "BotToken").ShouldBe("second");
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[1,2]")]
    public void GetString_MalformedConfig_ReturnsNull(string configJson)
    {
        SetupConfigReader.GetString(configJson, "BotToken").ShouldBeNull();
        SetupConfigReader.GetAllValuesOf(configJson, ["BotToken"]).ShouldBeEmpty();
    }

    [Test]
    public void Redactor_RemovesConfiguredValuesButKeepsWebhookUrl()
    {
        const string webhookUrl = "https://klacks.example.com/api/messaging/webhook/telegram";
        var redactor = new SetupSecretRedactor(MessagingConstants.ProviderTelegram,
            $"{{\"BotToken\":\"SECRET-123\",\"BotToken\":\"SECRET-456\",\"WebhookUrl\":\"{webhookUrl}\"}}", "WEBHOOK-SECRET");

        var cleaned = redactor.Clean($"token SECRET-123 / SECRET-456 / WEBHOOK-SECRET at {webhookUrl}");

        cleaned.ShouldNotBeNull();
        cleaned.ShouldNotContain("SECRET-123");
        cleaned.ShouldNotContain("SECRET-456");
        cleaned.ShouldNotContain("WEBHOOK-SECRET");
        cleaned.ShouldContain(webhookUrl);
    }

    [Test]
    public void Redactor_KeepsIdentifiersReadableAndRemovesOnlyCredentials()
    {
        var redactor = new SetupSecretRedactor(MessagingConstants.ProviderWhatsApp,
            "{\"AccessToken\":\"EAAG-access-token\",\"PhoneNumberId\":\"109876543210987\",\"AppSecret\":\"app-secret-value\",\"VerifyToken\":\"vt\"}",
            null);

        var cleaned = redactor.Clean("phone 109876543210987 rejected token EAAG-access-token, secret app-secret-value, verify vt!");

        cleaned.ShouldNotBeNull();
        cleaned.ShouldContain("109876543210987");
        cleaned.ShouldNotContain("EAAG-access-token");
        cleaned.ShouldNotContain("app-secret-value");
        cleaned.ShouldNotContain("vt!");
    }

    [Test]
    public void Redactor_Teams_RedactsTheWebhookUrlBecauseItIsTheCredential()
    {
        const string teamsUrl = "https://example.webhook.office.com/webhookb2/abc@def/IncomingWebhook/123/456";
        var redactor = new SetupSecretRedactor(MessagingConstants.ProviderTeams, $"{{\"WebhookUrl\":\"{teamsUrl}\"}}", null);

        var cleaned = redactor.Clean($"POST {teamsUrl} failed with 400");

        cleaned.ShouldNotBeNull();
        cleaned.ShouldNotContain(teamsUrl);
        cleaned.ShouldContain(SetupStepDetails.RedactedPlaceholder);
    }
}
