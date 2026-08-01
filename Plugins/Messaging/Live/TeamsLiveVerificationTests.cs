// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live verification of TeamsMessagingProvider against a real Power Automate workflow webhook.
/// Marked Explicit, so it never runs in the normal suite or in CI. The webhook URL comes from an
/// environment variable and is never stored in the repository - treat it like a password, since
/// anyone holding it can post into the channel.
///
/// Run with:
///   powershell -File run-live-test.ps1 -Provider Teams
///
/// EVERY RUN POSTS TWO VISIBLE CARDS into the configured Teams channel. ValidateConfigAsync has
/// no read-only path - it posts "Klacks connection test" - so validation and sending are one
/// visible message each. The fixture is deliberately kept to those two; a third step would just
/// be noise in someone's channel.
///
/// What this fixture cannot prove: a Power Automate flow answers the HTTP request without
/// returning a message identifier, and SendMessageResult carries none for Teams. Success=true
/// therefore means "the flow accepted the request", never "the card rendered in the channel".
/// The only real confirmation is looking at the channel.
///
/// Teams is outbound-only by design: the recipient is ignored, every message goes to the one
/// channel behind the webhook, ValidateWebhook returns false and ParseWebhookPayload returns
/// null. The shipped manual says so. The absent inbound steps are not an oversight.
/// </summary>
using System.Text.Json;
using Klacks.Plugin.Messaging.Infrastructure.Http;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Live;

[TestFixture]
[Explicit("Posts real cards into a real Teams channel. Requires TEAMS_WEBHOOK_URL.")]
[Category("Live")]
public class TeamsLiveVerificationTests
{
    private const string WebhookUrlVariable = "TEAMS_WEBHOOK_URL";

    // Any recipient is ignored by the adapter; passing one documents that.
    private const string IgnoredRecipient = "ignored-by-teams";

    private const string CorruptedSignatureSuffix = "-klacks-invalid";

    private HttpClient? _httpClient;
    private TeamsMessagingProvider _provider = null!;
    private string _webhookUrl = null!;
    private string _configJson = null!;

    [SetUp]
    public void Setup()
    {
        _webhookUrl = RequireVariable(WebhookUrlVariable);
        _configJson = JsonSerializer.Serialize(new { WebhookUrl = _webhookUrl });

        var retryHandler = new RateLimitRetryHandler(NullLogger<RateLimitRetryHandler>.Instance)
        {
            InnerHandler = new HttpClientHandler()
        };
        _httpClient = new HttpClient(retryHandler);
        _provider = new TeamsMessagingProvider(_httpClient, NullLogger<TeamsMessagingProvider>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
        _httpClient = null;
    }

    [Test]
    [Order(1)]
    public async Task Step1_ValidateConfig_Posts_The_Connection_Test_Card()
    {
        var isValid = await _provider.ValidateConfigAsync(_configJson);

        isValid.ShouldBeTrue(
            $"the workflow behind {WebhookUrlVariable} rejected the connection test card. The URL is " +
            "reissued whenever the flow is recreated, and a disabled flow fails the same way.");

        TestContext.Out.WriteLine("A 'Klacks connection test' card should now be visible in the channel.");
    }

    [Test]
    [Order(2)]
    public async Task Step2_SendAsync_Posts_An_Adaptive_Card_To_The_Channel()
    {
        var text = $"Klacks live verification - outbound send, run {TestContext.CurrentContext.Test.ID}";

        var result = await _provider.SendAsync(new(IgnoredRecipient, text), _configJson);

        TestContext.Out.WriteLine($"Teams response: success={result.Success} error={result.ErrorMessage}");
        TestContext.Out.WriteLine(
            "Confirm in the channel that the card actually rendered - the flow returns no message id, " +
            "so a passing test only proves the request was accepted.");

        result.Success.ShouldBeTrue($"the workflow refused the card: {result.ErrorMessage}");
        result.IsThrottled.ShouldBeFalse();

        // Documents the structural gap rather than asserting around it: the adapter has no id to
        // report because Power Automate returns none.
        result.ExternalMessageId.ShouldBeNull(
            "Teams unexpectedly produced a message id - if the flow now returns one, the adapter " +
            "should carry it so delivery becomes verifiable");
    }

    [Test]
    [Order(3)]
    public async Task Step3_SendAsync_Reports_A_Real_Failure_For_A_Corrupted_Webhook_Url()
    {
        var brokenConfig = JsonSerializer.Serialize(new { WebhookUrl = _webhookUrl + CorruptedSignatureSuffix });

        var result = await _provider.SendAsync(new(IgnoredRecipient, "Klacks live verification - error path"), brokenConfig);

        TestContext.Out.WriteLine($"Teams error response: success={result.Success} error={result.ErrorMessage}");

        result.Success.ShouldBeFalse(
            "Power Automate accepted a URL with a corrupted signature, so the error path is untested");
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace(
            "the failure was not surfaced to the caller, so a broken webhook URL would look like a successful send");
    }

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Ignore($"Environment variable {name} is not set. See the class summary for how to run this.");

        return value!;
    }
}
