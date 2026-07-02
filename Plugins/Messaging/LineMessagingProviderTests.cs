// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for LineMessagingProvider covering push message sending, config validation,
/// x-line-signature webhook validation and webhook event payload parsing.
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
public class LineMessagingProviderTests
{
    private const string ChannelAccessToken = "test-channel-access-token";
    private const string ChannelSecret = "test-channel-secret";
    private const string ConfigWithToken = "{\"ChannelAccessToken\":\"test-channel-access-token\"}";
    private const string ConfigWithSecret = "{\"ChannelAccessToken\":\"test-channel-access-token\",\"ChannelSecret\":\"test-channel-secret\"}";
    private const string SignatureHeader = "x-line-signature";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<LineMessagingProvider> _logger = null!;
    private LineMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<LineMessagingProvider>>();
        _sut = new LineMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_Returns_Success_With_SentMessageId_And_Sends_Bearer_And_Payload()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"sentMessages\":[{\"id\":\"461230966842064897\",\"quoteToken\":\"IStG5h1Tz7b\"}]}");
        var request = new SendMessageRequest("U4af4980629", "Hello LINE");

        // Act
        var result = await _sut.SendAsync(request, ConfigWithToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("461230966842064897");
        _handler.LastRequestUri.ShouldBe("https://api.line.me/v2/bot/message/push");
        _handler.LastAuthorizationHeader.ShouldBe($"Bearer {ChannelAccessToken}");
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("to").GetString().ShouldBe("U4af4980629");
        var messages = payload.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(1);
        messages[0].GetProperty("type").GetString().ShouldBe("text");
        messages[0].GetProperty("text").GetString().ShouldBe("Hello LINE");
    }

    [Test]
    public async Task SendAsync_Returns_Success_With_Null_ExternalMessageId_When_Response_Has_No_SentMessages()
    {
        // Arrange
        _handler.Response = JsonResponse("{}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("U4af4980629", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBeNull();
    }

    [Test]
    public async Task SendAsync_Returns_Failure_With_Error_Detail_When_Api_Returns_400()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"message\":\"The property, 'to', in the request body is invalid\"}", Encoding.UTF8, "application/json")
        };

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("invalid", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("BadRequest");
        result.ErrorMessage!.ShouldContain("The property, 'to', in the request body is invalid");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_ChannelAccessToken_Missing()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("U4af4980629", "Hello"), "{}");

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_When_BotInfo_Returns_Ok()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"userId\":\"Ub9b8\",\"basicId\":\"@klacks\",\"displayName\":\"Klacks Bot\"}");

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestUri.ShouldBe("https://api.line.me/v2/bot/info");
        _handler.LastAuthorizationHeader.ShouldBe($"Bearer {ChannelAccessToken}");
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_When_BotInfo_Returns_Unauthorized()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"message\":\"invalid token\"}", Encoding.UTF8, "application/json")
        };

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Valid_For_Correct_Signature()
    {
        // Arrange
        var body = "{\"destination\":\"U0000\",\"events\":[]}";
        var signature = ComputeLineSignature(ChannelSecret, body);
        var context = BuildContext(body, signature, ConfigWithSecret);

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
        var body = "{\"destination\":\"U0000\",\"events\":[]}";
        var wrongSignature = Convert.ToBase64String(new byte[32]);
        var context = BuildContext(body, wrongSignature, ConfigWithSecret);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_When_Signature_Header_Missing()
    {
        // Arrange
        var context = BuildContext("{\"destination\":\"U0000\",\"events\":[]}", null, ConfigWithSecret);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_When_ChannelSecret_Missing()
    {
        // Arrange
        var body = "{\"destination\":\"U0000\",\"events\":[]}";
        var signature = ComputeLineSignature(ChannelSecret, body);
        var context = BuildContext(body, signature, ConfigWithToken);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ParseWebhookPayload_Maps_Text_Message_Event()
    {
        // Arrange
        var body = "{\"destination\":\"U0000\",\"events\":[{\"type\":\"message\",\"message\":{\"type\":\"text\",\"id\":\"468789577898262530\",\"text\":\"Hello LINE\"},\"source\":{\"type\":\"user\",\"userId\":\"U4af4980629\"},\"replyToken\":\"reply-token\"}]}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldNotBeNull();
        result!.Sender.ShouldBe("U4af4980629");
        result.SenderDisplayName.ShouldBe("U4af4980629");
        result.Content.ShouldBe("Hello LINE");
        result.ExternalMessageId.ShouldBe("468789577898262530");
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_Follow_Event()
    {
        // Arrange
        var body = "{\"destination\":\"U0000\",\"events\":[{\"type\":\"follow\",\"source\":{\"type\":\"user\",\"userId\":\"U4af4980629\"},\"replyToken\":\"reply-token\"}]}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_Sticker_Message()
    {
        // Arrange
        var body = "{\"destination\":\"U0000\",\"events\":[{\"type\":\"message\",\"message\":{\"type\":\"sticker\",\"id\":\"468789577898262531\",\"packageId\":\"1\",\"stickerId\":\"2\"},\"source\":{\"type\":\"user\",\"userId\":\"U4af4980629\"},\"replyToken\":\"reply-token\"}]}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_Empty_Events_Array()
    {
        // Arrange
        var body = "{\"destination\":\"U0000\",\"events\":[]}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_When_UserId_Missing()
    {
        // Arrange
        var body = "{\"destination\":\"U0000\",\"events\":[{\"type\":\"message\",\"message\":{\"type\":\"text\",\"id\":\"468789577898262532\",\"text\":\"Hello\"},\"source\":{\"type\":\"group\",\"groupId\":\"G123\"},\"replyToken\":\"reply-token\"}]}";

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

    private static string ComputeLineSignature(string channelSecret, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(hash);
    }

    private static WebhookValidationContext BuildContext(string body, string? signature, string configJson)
    {
        var headers = new Dictionary<string, string>();
        if (signature != null)
        {
            headers[SignatureHeader] = signature;
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
