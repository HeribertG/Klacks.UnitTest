// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ViberMessagingProvider covering message sending, config validation,
/// webhook registration, webhook signature validation and message payload parsing.
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
public class ViberMessagingProviderTests
{
    private const string AuthToken = "viber-test-auth-token";
    private const string AuthTokenHeader = "X-Viber-Auth-Token";
    private const string SignatureHeader = "X-Viber-Content-Signature";
    private const string WebhookUrl = "https://example.com/api/webhooks/viber";
    private const string ConfigWithToken = "{\"AuthToken\":\"viber-test-auth-token\"}";
    private const string ConfigWithSenderName = "{\"AuthToken\":\"viber-test-auth-token\",\"SenderName\":\"Klacks Bot\"}";
    private const string ConfigWithWebhookUrl = "{\"AuthToken\":\"viber-test-auth-token\",\"WebhookUrl\":\"https://example.com/api/webhooks/viber\"}";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<ViberMessagingProvider> _logger = null!;
    private ViberMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<ViberMessagingProvider>>();
        _sut = new ViberMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_Returns_Success_With_MessageToken_And_Sends_AuthHeader_And_Payload()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"status\":0,\"status_message\":\"ok\",\"message_token\":4912661846655238145}");
        var request = new SendMessageRequest("01234567890A=", "Hello Viber");

        // Act
        var result = await _sut.SendAsync(request, ConfigWithSenderName);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("4912661846655238145");
        _handler.LastRequestUri.ShouldBe("https://chatapi.viber.com/pa/send_message");
        _handler.LastViberAuthToken.ShouldBe(AuthToken);
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("receiver").GetString().ShouldBe("01234567890A=");
        payload.GetProperty("type").GetString().ShouldBe("text");
        payload.GetProperty("text").GetString().ShouldBe("Hello Viber");
        payload.GetProperty("sender").GetProperty("name").GetString().ShouldBe("Klacks Bot");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_When_Response_Is_Http200_But_Status_Not_Zero()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"status\":2,\"status_message\":\"invalidAuthToken\"}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("01234567890A=", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("invalidAuthToken");
    }

    [Test]
    public async Task SendAsync_Uses_Fallback_SenderName_When_Config_SenderName_Empty()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"status\":0,\"message_token\":123}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("01234567890A=", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("sender").GetProperty("name").GetString().ShouldBe("Klacks");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_AuthToken_Missing()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("01234567890A=", "Hello"), "{}");

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_When_AccountInfo_Status_Zero()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"status\":0,\"status_message\":\"ok\",\"name\":\"Klacks Bot\"}");

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestUri.ShouldBe("https://chatapi.viber.com/pa/get_account_info");
        _handler.LastViberAuthToken.ShouldBe(AuthToken);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_When_AccountInfo_Status_Not_Zero()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"status\":2,\"status_message\":\"invalidAuthToken\"}");

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task RegisterWebhookAsync_Returns_True_And_Sends_Url_And_EventTypes()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"status\":0,\"status_message\":\"ok\",\"event_types\":[\"message\"]}");

        // Act
        var result = await _sut.RegisterWebhookAsync(ConfigWithWebhookUrl, "unused-secret");

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestUri.ShouldBe("https://chatapi.viber.com/pa/set_webhook");
        _handler.LastViberAuthToken.ShouldBe(AuthToken);
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("url").GetString().ShouldBe(WebhookUrl);
        payload.GetProperty("event_types")[0].GetString().ShouldBe("message");
        payload.GetProperty("send_name").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task RegisterWebhookAsync_Returns_False_Without_Http_Call_When_WebhookUrl_Missing()
    {
        // Act
        var result = await _sut.RegisterWebhookAsync(ConfigWithToken, "unused-secret");

        // Assert
        result.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task RegisterWebhookAsync_Returns_False_When_Status_Not_Zero()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"status\":1,\"status_message\":\"invalidUrl\"}");

        // Act
        var result = await _sut.RegisterWebhookAsync(ConfigWithWebhookUrl, "unused-secret");

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Valid_For_Correct_Signature()
    {
        // Arrange
        var body = "{\"event\":\"message\"}";
        var signature = ComputeViberSignature(AuthToken, body);
        var context = BuildContext(body, signature, ConfigWithToken);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public void ValidateWebhook_Returns_Valid_For_Uppercase_Signature()
    {
        // Arrange
        var body = "{\"event\":\"message\"}";
        var signature = ComputeViberSignature(AuthToken, body).ToUpperInvariant();
        var context = BuildContext(body, signature, ConfigWithToken);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_For_Wrong_Signature()
    {
        // Arrange
        var body = "{\"event\":\"message\"}";
        var wrongSignature = new string('0', 64);
        var context = BuildContext(body, wrongSignature, ConfigWithToken);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_When_Signature_Header_Missing()
    {
        // Arrange
        var context = BuildContext("{\"event\":\"message\"}", null, ConfigWithToken);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_When_AuthToken_Missing()
    {
        // Arrange
        var body = "{\"event\":\"message\"}";
        var signature = ComputeViberSignature(AuthToken, body);
        var context = BuildContext(body, signature, "{}");

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ParseWebhookPayload_Maps_Message_Event()
    {
        // Arrange
        var body = "{\"event\":\"message\",\"timestamp\":1457764197627,\"message_token\":4912661846655238145,\"sender\":{\"id\":\"01234567890A=\",\"name\":\"John McClane\"},\"message\":{\"type\":\"text\",\"text\":\"Hello Klacks\"}}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldNotBeNull();
        result!.Sender.ShouldBe("01234567890A=");
        result.SenderDisplayName.ShouldBe("John McClane");
        result.Content.ShouldBe("Hello Klacks");
        result.ExternalMessageId.ShouldBe("4912661846655238145");
    }

    [Test]
    public void ParseWebhookPayload_Uses_SenderId_As_DisplayName_When_Name_Missing()
    {
        // Arrange
        var body = "{\"event\":\"message\",\"message_token\":123,\"sender\":{\"id\":\"01234567890A=\"},\"message\":{\"type\":\"text\",\"text\":\"Hi\"}}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldNotBeNull();
        result!.SenderDisplayName.ShouldBe("01234567890A=");
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_For_NonText_Message()
    {
        // Arrange
        var body = "{\"event\":\"message\",\"message_token\":123,\"sender\":{\"id\":\"01234567890A=\",\"name\":\"John\"},\"message\":{\"type\":\"picture\",\"media\":\"https://example.com/img.jpg\"}}";

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [TestCase("{\"event\":\"seen\",\"timestamp\":1457764197627,\"message_token\":4912661846655238145,\"user_id\":\"01234567890A=\"}")]
    [TestCase("{\"event\":\"delivered\",\"timestamp\":1457764197627,\"message_token\":4912661846655238145,\"user_id\":\"01234567890A=\"}")]
    [TestCase("{\"event\":\"subscribed\",\"timestamp\":1457764197627,\"user\":{\"id\":\"01234567890A=\",\"name\":\"John\"}}")]
    [TestCase("{\"event\":\"conversation_started\",\"timestamp\":1457764197627,\"user\":{\"id\":\"01234567890A=\",\"name\":\"John\"}}")]
    [TestCase("{\"event\":\"webhook\",\"timestamp\":1457764197627}")]
    public void ParseWebhookPayload_Returns_Null_For_Non_Message_Events(string body)
    {
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

    private static string ComputeViberSignature(string authToken, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexStringLower(hash);
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
        public string? LastViberAuthToken { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastViberAuthToken = request.Headers.TryGetValues(AuthTokenHeader, out var values)
                ? string.Join(",", values)
                : null;
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response;
        }
    }
}
