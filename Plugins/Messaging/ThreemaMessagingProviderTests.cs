// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ThreemaMessagingProvider covering send_simple requests in gateway basic mode,
/// error status mapping, credits-based config validation and webhook behavior.
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
public class ThreemaMessagingProviderTests
{
    private const string ValidConfig = "{\"GatewayId\":\"*COMPANY\",\"ApiSecret\":\"gatewaySecret\"}";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<ThreemaMessagingProvider> _logger = null!;
    private ThreemaMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<ThreemaMessagingProvider>>();
        _sut = new ThreemaMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_With_Valid_Config_Sends_Correct_Request_And_Returns_MessageId()
    {
        // Arrange
        _handler.Response = TextResponse(HttpStatusCode.OK, "0102030405060708");
        var request = new SendMessageRequest("ECHOECHO", "Hello from Klacks");

        // Act
        var result = await _sut.SendAsync(request, ValidConfig);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("0102030405060708");
        result.ErrorMessage.ShouldBeNull();
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        _handler.LastRequestUri.ShouldBe("https://msgapi.threema.ch/send_simple");
        var form = ParseFormBody(_handler.LastRequestBody!);
        form["from"].ShouldBe("*COMPANY");
        form["to"].ShouldBe("ECHOECHO");
        form["text"].ShouldBe("Hello from Klacks");
        form["secret"].ShouldBe("gatewaySecret");
    }

    [TestCase(HttpStatusCode.BadRequest, "Threema gateway error: invalid recipient or identity")]
    [TestCase(HttpStatusCode.Unauthorized, "Threema gateway error: invalid gateway credentials")]
    [TestCase(HttpStatusCode.PaymentRequired, "Threema gateway error: no gateway credits left")]
    [TestCase(HttpStatusCode.NotFound, "Threema gateway error: recipient not found")]
    public async Task SendAsync_On_Known_Error_Status_Returns_Specific_Error_Message(
        HttpStatusCode statusCode, string expectedError)
    {
        // Arrange
        _handler.Response = TextResponse(statusCode, string.Empty);

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("ECHOECHO", "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ExternalMessageId.ShouldBeNull();
        result.ErrorMessage.ShouldBe(expectedError);
    }

    [Test]
    public async Task SendAsync_On_Unknown_Error_Status_Returns_Generic_Error_With_StatusCode()
    {
        // Arrange
        _handler.Response = TextResponse(HttpStatusCode.InternalServerError, string.Empty);

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("ECHOECHO", "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Threema gateway error");
        result.ErrorMessage.ShouldContain(HttpStatusCode.InternalServerError.ToString());
    }

    [TestCase("{}")]
    [TestCase("{\"ApiSecret\":\"gatewaySecret\"}")]
    [TestCase("{\"GatewayId\":\"*COMPANY\"}")]
    [TestCase("not-json")]
    public async Task SendAsync_With_Missing_Or_Invalid_Config_Fails_Without_Http_Call(string config)
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("ECHOECHO", "Hello"), config);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_On_Success_Response()
    {
        // Arrange
        _handler.Response = TextResponse(HttpStatusCode.OK, "42");

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Get);
        _handler.LastRequestUri.ShouldNotBeNull();
        _handler.LastRequestUri.ShouldStartWith("https://msgapi.threema.ch/credits?");
        var query = ParseQuery(_handler.LastRequestUri!);
        query["from"].ShouldBe("*COMPANY");
        query["secret"].ShouldBe("gatewaySecret");
    }

    [Test]
    public async Task ValidateConfigAsync_Encodes_Query_Parameter_Values()
    {
        // Arrange
        _handler.Response = TextResponse(HttpStatusCode.OK, "42");
        const string config = "{\"GatewayId\":\"*COMPANY\",\"ApiSecret\":\"sec&ret=+x\"}";

        // Act
        var result = await _sut.ValidateConfigAsync(config);

        // Assert
        result.ShouldBeTrue();
        var query = ParseQuery(_handler.LastRequestUri!);
        query["from"].ShouldBe("*COMPANY");
        query["secret"].ShouldBe("sec&ret=+x");
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

    [TestCase("{}")]
    [TestCase("{\"ApiSecret\":\"gatewaySecret\"}")]
    [TestCase("{\"GatewayId\":\"*COMPANY\"}")]
    public async Task ValidateConfigAsync_With_Missing_Required_Fields_Returns_False_Without_Http_Call(string config)
    {
        // Act
        var result = await _sut.ValidateConfigAsync(config);

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
        var result = _sut.ParseWebhookPayload("{\"from\":\"ECHOECHO\",\"box\":\"encrypted\"}");

        // Assert
        result.ShouldBeNull();
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
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

    private static Dictionary<string, string> ParseQuery(string requestUri)
    {
        var query = requestUri.Split('?', 2)[1];
        return query.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public int CallCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestMethod = request.Method;
            LastRequestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return Response;
        }
    }
}
