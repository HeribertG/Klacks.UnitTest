// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SmsMessagingProvider covering Twilio-compatible send requests, custom gateway URLs,
/// error mapping, config validation and webhook behavior.
/// </summary>
using System.Net;
using System.Text;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SmsMessagingProviderTests
{
    private const string ValidConfig =
        "{\"AccountSid\":\"AC123\",\"AuthToken\":\"secret\",\"SenderNumber\":\"+41790000000\"}";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<SmsMessagingProvider> _logger = null!;
    private SmsMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<SmsMessagingProvider>>();
        _sut = new SmsMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_With_Valid_Config_Sends_Correct_Request_And_Returns_Sid()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.Created, "{\"sid\":\"SM123abc\"}");
        var request = new SendMessageRequest("+41791112233", "Hello");

        // Act
        var result = await _sut.SendAsync(request, ValidConfig);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("SM123abc");
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        _handler.LastRequestUri.ShouldBe("https://api.twilio.com/2010-04-01/Accounts/AC123/Messages.json");
        var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("AC123:secret"));
        _handler.LastAuthorizationHeader.ShouldBe(expectedAuth);
        var form = ParseFormBody(_handler.LastRequestBody!);
        form["To"].ShouldBe("+41791112233");
        form["From"].ShouldBe("+41790000000");
        form["Body"].ShouldBe("Hello");
    }

    [Test]
    public async Task SendAsync_With_Custom_GatewayUrl_Uses_Gateway_Base_Without_Double_Slash()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.Created, "{\"sid\":\"SM456\"}");
        const string config =
            "{\"AccountSid\":\"AC123\",\"AuthToken\":\"secret\",\"SenderNumber\":\"+41790000000\",\"GatewayUrl\":\"https://gateway.example.com/\"}";

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("+41791112233", "Hello"), config);

        // Assert
        result.Success.ShouldBeTrue();
        _handler.LastRequestUri.ShouldBe("https://gateway.example.com/2010-04-01/Accounts/AC123/Messages.json");
    }

    [Test]
    public async Task SendAsync_On_Error_Response_Returns_Twilio_Error_Message_With_StatusCode()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.BadRequest,
            "{\"message\":\"The 'To' number is not valid\",\"code\":21211}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("invalid", "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ExternalMessageId.ShouldBeNull();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("The 'To' number is not valid");
        result.ErrorMessage.ShouldContain(HttpStatusCode.BadRequest.ToString());
    }

    [TestCase("{}")]
    [TestCase("{\"AuthToken\":\"secret\",\"SenderNumber\":\"+41790000000\"}")]
    [TestCase("{\"AccountSid\":\"AC123\",\"SenderNumber\":\"+41790000000\"}")]
    [TestCase("{\"AccountSid\":\"AC123\",\"AuthToken\":\"secret\"}")]
    public async Task SendAsync_With_Missing_Required_Fields_Fails_Without_Http_Call(string config)
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("+41791112233", "Hello"), config);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_On_Success_Response()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"sid\":\"AC123\",\"status\":\"active\"}");

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Get);
        _handler.LastRequestUri.ShouldBe("https://api.twilio.com/2010-04-01/Accounts/AC123.json");
        var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("AC123:secret"));
        _handler.LastAuthorizationHeader.ShouldBe(expectedAuth);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_On_Unauthorized()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task ValidateConfigAsync_With_Missing_Required_Fields_Returns_False_Without_Http_Call()
    {
        // Act
        var result = await _sut.ValidateConfigAsync("{\"AccountSid\":\"AC123\"}");

        // Assert
        result.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public void ValidateWebhook_Always_Returns_Invalid()
    {
        // Arrange
        var context = new WebhookValidationContext(
            "body",
            new Dictionary<string, string>(),
            ValidConfig,
            "secret");

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ParseWebhookPayload_Always_Returns_Null()
    {
        // Act
        var result = _sut.ParseWebhookPayload("{\"From\":\"+41791112233\",\"Body\":\"Hi\"}");

        // Assert
        result.ShouldBeNull();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static Dictionary<string, string> ParseFormBody(string body)
    {
        return body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : string.Empty);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public int CallCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestMethod = request.Method;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastRequestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return Response;
        }
    }
}
