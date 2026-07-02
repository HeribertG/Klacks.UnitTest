// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SlackMessagingProvider covering message sending, config validation,
/// webhook signature validation and event payload parsing.
/// </summary>
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SlackMessagingProviderTests
{
    private const string BotToken = "xoxb-test-token";
    private const string TestSigningSecret = "8f742231b10e8888abcd99yyyzzz85a5";
    private const string ConfigWithToken = "{\"BotToken\":\"xoxb-test-token\"}";
    private const string ConfigWithSigningSecret = "{\"BotToken\":\"xoxb-test-token\",\"SigningSecret\":\"8f742231b10e8888abcd99yyyzzz85a5\"}";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<SlackMessagingProvider> _logger = null!;
    private SlackMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<SlackMessagingProvider>>();
        _sut = new SlackMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_Returns_Success_With_Ts_And_Sends_Bearer_And_Channel()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"ok\":true,\"channel\":\"C123\",\"ts\":\"1712345678.000100\"}");
        var request = new SendMessageRequest("C123", "Hello Slack");

        // Act
        var result = await _sut.SendAsync(request, ConfigWithToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("1712345678.000100");
        _handler.LastRequestUri.ShouldBe("https://slack.com/api/chat.postMessage");
        _handler.LastAuthorizationHeader.ShouldBe($"Bearer {BotToken}");
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("channel").GetString().ShouldBe("C123");
        payload.GetProperty("text").GetString().ShouldBe("Hello Slack");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_When_Response_Is_Http200_But_Ok_False()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"ok\":false,\"error\":\"channel_not_found\"}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("C404", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("channel_not_found");
    }

    [Test]
    public async Task SendAsync_Uses_DefaultChannel_When_Recipient_Is_Empty()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"ok\":true,\"ts\":\"1712345678.000200\"}");
        var config = "{\"BotToken\":\"xoxb-test-token\",\"DefaultChannel\":\"C999\"}";

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(string.Empty, "Hello"), config);

        // Assert
        result.Success.ShouldBeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("channel").GetString().ShouldBe("C999");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_No_Channel_Available()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(string.Empty, "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_BotToken_Missing()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("C123", "Hello"), "{}");

        // Assert
        result.Success.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_When_AuthTest_Ok()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"ok\":true,\"user\":\"klacks_bot\"}");

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestUri.ShouldBe("https://slack.com/api/auth.test");
        _handler.LastAuthorizationHeader.ShouldBe($"Bearer {BotToken}");
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_When_AuthTest_Not_Ok()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"ok\":false,\"error\":\"invalid_auth\"}");

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Valid_For_Correct_Signature()
    {
        // Arrange
        var body = "{\"type\":\"event_callback\"}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeSlackSignature(TestSigningSecret, timestamp, body);
        var context = BuildContext(body, signature, timestamp, ConfigWithSigningSecret);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.ChallengeResponse.ShouldBeNull();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_For_Wrong_Signature()
    {
        // Arrange
        var body = "{\"type\":\"event_callback\"}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var wrongSignature = $"v0={new string('0', 64)}";
        var context = BuildContext(body, wrongSignature, timestamp, ConfigWithSigningSecret);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_For_Stale_Timestamp()
    {
        // Arrange
        var body = "{\"type\":\"event_callback\"}";
        var timestamp = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400).ToString();
        var signature = ComputeSlackSignature(TestSigningSecret, timestamp, body);
        var context = BuildContext(body, signature, timestamp, ConfigWithSigningSecret);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_When_SigningSecret_Missing()
    {
        // Arrange
        var body = "{\"type\":\"event_callback\"}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeSlackSignature(TestSigningSecret, timestamp, body);
        var context = BuildContext(body, signature, timestamp, ConfigWithToken);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_When_Signature_Headers_Missing()
    {
        // Arrange
        var context = BuildContext("{\"type\":\"event_callback\"}", null, null, ConfigWithSigningSecret);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Challenge_For_Url_Verification_With_Valid_Signature()
    {
        // Arrange
        var body = "{\"type\":\"url_verification\",\"token\":\"tok\",\"challenge\":\"ch4ll3ng3\"}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeSlackSignature(TestSigningSecret, timestamp, body);
        var context = BuildContext(body, signature, timestamp, ConfigWithSigningSecret);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.ChallengeResponse.ShouldBe("ch4ll3ng3");
    }

    [Test]
    public void ParseWebhookPayload_Maps_Message_Event()
    {
        // Arrange
        var body = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"user\":\"U123\",\"text\":\"Hello\",\"ts\":\"1712345678.000300\",\"channel\":\"C123\"}}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldNotBeNull();
        result!.Sender.ShouldBe("U123");
        result.SenderDisplayName.ShouldBe("U123");
        result.Content.ShouldBe("Hello");
        result.ExternalMessageId.ShouldBe("1712345678.000300");
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_Bot_Message()
    {
        // Arrange
        var body = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"bot_id\":\"B123\",\"user\":\"U123\",\"text\":\"Echo\",\"ts\":\"1712345678.000400\"}}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_Subtype_Event()
    {
        // Arrange
        var body = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"subtype\":\"message_changed\",\"user\":\"U123\",\"text\":\"Edited\",\"ts\":\"1712345678.000500\"}}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_Url_Verification()
    {
        // Arrange
        var body = "{\"type\":\"url_verification\",\"challenge\":\"ch4ll3ng3\"}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_Garbage()
    {
        // Act
        var result = _sut.ParseWebhookPayload("this is not json");

        // Assert
        result.ShouldBeNull();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string ComputeSlackSignature(string signingSecret, string timestamp, string body)
    {
        var baseString = $"v0:{timestamp}:{body}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(baseString));
        return $"v0={Convert.ToHexStringLower(hash)}";
    }

    private static WebhookValidationContext BuildContext(string body, string? signature, string? timestamp, string configJson)
    {
        var headers = new Dictionary<string, string>();
        if (signature != null)
        {
            headers["X-Slack-Signature"] = signature;
        }

        if (timestamp != null)
        {
            headers["X-Slack-Request-Timestamp"] = timestamp;
        }

        return new WebhookValidationContext(body, headers, configJson, string.Empty);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public int CallCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response;
        }
    }
}
