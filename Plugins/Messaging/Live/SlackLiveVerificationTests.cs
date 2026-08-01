// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live verification of SlackMessagingProvider against the real Slack Web API. Marked Explicit,
/// so it never runs in the normal suite or in CI. Credentials come from environment variables
/// and are never stored in the repository.
///
/// Run with:
///   SLACK_BOT_TOKEN=xoxb-... SLACK_CHANNEL=C0123456789 dotnet test
///     --filter "FullyQualifiedName~SlackLiveVerification"
///
/// Unlike the fixture-based provider tests, this one proves the endpoint, the payload shape and
/// the token actually work — the fixture tests only prove the code is consistent with our own
/// assumptions. The provider is built over the real handler pipeline, so the rate-limit handler
/// is exercised on the real path too.
/// </summary>
using Klacks.Plugin.Messaging.Infrastructure.Http;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Live;

[TestFixture]
[Explicit("Sends real messages to a real Slack workspace. Requires SLACK_BOT_TOKEN and SLACK_CHANNEL.")]
[Category("Live")]
public class SlackLiveVerificationTests
{
    private const string TokenVariable = "SLACK_BOT_TOKEN";
    private const string ChannelVariable = "SLACK_CHANNEL";
    private const string NonExistentChannel = "C00000000000";
    private const string SlackChannelNotFoundError = "channel_not_found";

    private HttpClient? _httpClient;
    private SlackMessagingProvider _provider = null!;
    private string _channel = null!;
    private string _configJson = null!;

    [SetUp]
    public void Setup()
    {
        var token = RequireVariable(TokenVariable);
        _channel = RequireVariable(ChannelVariable);
        _configJson = BuildConfig(token, _channel);

        var retryHandler = new RateLimitRetryHandler(NullLogger<RateLimitRetryHandler>.Instance)
        {
            InnerHandler = new HttpClientHandler()
        };
        _httpClient = new HttpClient(retryHandler);
        _provider = new SlackMessagingProvider(_httpClient, NullLogger<SlackMessagingProvider>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
        _httpClient = null;
    }

    [Test]
    public async Task Step1_ValidateConfig_Accepts_The_Real_Bot_Token()
    {
        var isValid = await _provider.ValidateConfigAsync(_configJson);

        isValid.ShouldBeTrue(
            $"auth.test rejected the token in {TokenVariable}. Check that the token starts with 'xoxb-' " +
            "and that the app is installed into the workspace.");
    }

    [Test]
    public async Task Step2_SendAsync_Delivers_A_Message_To_The_Real_Channel()
    {
        var text = $"Klacks live verification — outbound send, run {TestContext.CurrentContext.Test.ID}";

        var result = await _provider.SendAsync(new(_channel, text), _configJson);

        TestContext.Out.WriteLine($"Slack response: success={result.Success} id={result.ExternalMessageId} error={result.ErrorMessage}");

        result.Success.ShouldBeTrue($"Slack refused the send: {result.ErrorMessage}");
        result.ExternalMessageId.ShouldNotBeNullOrWhiteSpace("Slack returned no message timestamp (ts)");
        result.IsThrottled.ShouldBeFalse();
    }

    [Test]
    public async Task Step3_SendAsync_Reports_A_Real_Slack_Error_For_An_Unknown_Channel()
    {
        var result = await _provider.SendAsync(new(NonExistentChannel, "Klacks live verification — error path"), _configJson);

        TestContext.Out.WriteLine($"Slack error response: {result.ErrorMessage}");

        result.Success.ShouldBeFalse("Slack accepted a send to a non-existent channel, which means the error path is untested");
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain(SlackChannelNotFoundError);
        result.IsThrottled.ShouldBeFalse("a missing channel is a permanent error, not throttling");
    }

    [Test]
    public async Task Step4_SendAsync_Falls_Back_To_The_Configured_DefaultChannel()
    {
        var result = await _provider.SendAsync(new(string.Empty, "Klacks live verification — default channel fallback"), _configJson);

        result.Success.ShouldBeTrue($"Slack refused the DefaultChannel fallback: {result.ErrorMessage}");
    }

    private static string BuildConfig(string token, string channel)
    {
        var signingSecret = Environment.GetEnvironmentVariable("SLACK_SIGNING_SECRET") ?? string.Empty;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            BotToken = token,
            SigningSecret = signingSecret,
            DefaultChannel = channel,
        });
    }

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Ignore($"Environment variable {name} is not set. See the class summary for how to run this.");

        return value!;
    }
}
