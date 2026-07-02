// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SignalMessagingProvider covering send requests against a self-hosted
/// signal-cli-rest-api container, timestamp extraction, error mapping,
/// config validation against the accounts endpoint and webhook behavior.
/// </summary>
using System.Net;
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
public class SignalMessagingProviderTests
{
    private const string ValidConfig =
        "{\"SignalNumber\":\"+41790000000\",\"ApiUrl\":\"http://localhost:8080\"}";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<SignalMessagingProvider> _logger = null!;
    private SignalMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<SignalMessagingProvider>>();
        _sut = new SignalMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_With_Valid_Config_Sends_Correct_Request_And_Returns_Timestamp()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.Created, "{\"timestamp\": 1751400000000}");
        var request = new SendMessageRequest("+41791112233", "Hello");

        // Act
        var result = await _sut.SendAsync(request, ValidConfig);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("1751400000000");
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        _handler.LastRequestUri.ShouldBe("http://localhost:8080/v2/send");
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("message").GetString().ShouldBe("Hello");
        payload.GetProperty("number").GetString().ShouldBe("+41790000000");
        payload.GetProperty("recipients").GetArrayLength().ShouldBe(1);
        payload.GetProperty("recipients")[0].GetString().ShouldBe("+41791112233");
    }

    [Test]
    public async Task SendAsync_With_Http_Localhost_ApiUrl_And_Trailing_Slash_Trims_Slash()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.Created, "{\"timestamp\": 1751400000000}");
        const string config = "{\"SignalNumber\":\"+41790000000\",\"ApiUrl\":\"http://localhost:8080/\"}";

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("+41791112233", "Hello"), config);

        // Assert
        result.Success.ShouldBeTrue();
        _handler.LastRequestUri.ShouldBe("http://localhost:8080/v2/send");
    }

    [Test]
    public async Task SendAsync_Without_Timestamp_In_Response_Returns_Null_ExternalMessageId()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.Created, "{}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("+41791112233", "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBeNull();
    }

    [Test]
    public async Task SendAsync_On_Error_Response_Returns_Error_Detail_With_StatusCode()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.BadRequest,
            "{\"error\":\"Invalid recipient number\"}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("invalid", "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ExternalMessageId.ShouldBeNull();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Invalid recipient number");
        result.ErrorMessage.ShouldContain(HttpStatusCode.BadRequest.ToString());
    }

    [TestCase("{}")]
    [TestCase("{\"SignalNumber\":\"+41790000000\"}")]
    [TestCase("{\"ApiUrl\":\"http://localhost:8080\"}")]
    [TestCase("not json")]
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
    public async Task ValidateConfigAsync_Returns_True_When_Number_Is_In_Accounts_List()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.OK, "[\"+41790000000\",\"+41791111111\"]");

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Get);
        _handler.LastRequestUri.ShouldBe("http://localhost:8080/v1/accounts");
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_When_Number_Is_Not_In_Accounts_List()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.OK, "[\"+41791111111\"]");

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_When_Response_Is_Not_A_Json_Array()
    {
        // Arrange
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"accounts\":[\"+41790000000\"]}");

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_On_Error_Response()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task ValidateConfigAsync_With_Missing_Required_Fields_Returns_False_Without_Http_Call()
    {
        // Act
        var result = await _sut.ValidateConfigAsync("{\"SignalNumber\":\"+41790000000\"}");

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
        var result = _sut.ParseWebhookPayload("{\"envelope\":{\"source\":\"+41791112233\"}}");

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
