// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ZaloMessagingProvider covering customer service message sending via the
/// Zalo OA API v3 (HTTP 200 with error-code body pattern), config validation via getoa
/// and the unsupported webhook surface.
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
public class ZaloMessagingProviderTests
{
    private const string AccessToken = "zalo-test-access-token";
    private const string ConfigWithToken = "{\"AccessToken\":\"zalo-test-access-token\",\"OaId\":\"1234567890\"}";
    private const string SendMessageUrl = "https://openapi.zalo.me/v3.0/oa/message/cs";
    private const string GetOaUrl = "https://openapi.zalo.me/v2.0/oa/getoa";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<ZaloMessagingProvider> _logger = null!;
    private ZaloMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<ZaloMessagingProvider>>();
        _sut = new ZaloMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_Returns_Success_With_MessageId_And_Sends_AccessToken_Header()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"error\":0,\"message\":\"Success\",\"data\":{\"message_id\":\"msg-abc-123\"}}");
        var request = new SendMessageRequest("user-987", "Hello Zalo");

        // Act
        var result = await _sut.SendAsync(request, ConfigWithToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("msg-abc-123");
        _handler.LastRequestUri.ShouldBe(SendMessageUrl);
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        _handler.LastAccessTokenHeader.ShouldBe(AccessToken);
        _handler.LastAuthorizationHeader.ShouldBeNull();
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.LastRequestBody!);
        payload.GetProperty("recipient").GetProperty("user_id").GetString().ShouldBe("user-987");
        payload.GetProperty("message").GetProperty("text").GetString().ShouldBe("Hello Zalo");
    }

    [Test]
    public async Task SendAsync_Returns_Success_Without_MessageId_When_Data_Is_Missing()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"error\":0,\"message\":\"Success\"}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("user-987", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBeNull();
    }

    [Test]
    public async Task SendAsync_Returns_Failure_When_Response_Has_No_Error_Code()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"message\":\"Success\",\"data\":{\"message_id\":\"msg-1\"}}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("user-987", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("unexpected response without error code");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_When_User_Has_Not_Interacted()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"error\":-213,\"message\":\"User hasn't interacted with OA in the last 7 days\"}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("user-987", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("-213");
        result.ErrorMessage!.ShouldContain("User hasn't interacted with OA in the last 7 days");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_When_Access_Token_Is_Invalid()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"error\":-216,\"message\":\"Access token is invalid\"}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("user-987", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("-216");
        result.ErrorMessage!.ShouldContain("Access token is invalid");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_When_Http_Status_Is_Not_Success()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":-32,\"message\":\"Api unavailable\"}", Encoding.UTF8, "application/json")
        };

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("user-987", "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_AccessToken_Missing()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("user-987", "Hello"), "{}");

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_Config_Is_Invalid_Json()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("user-987", "Hello"), "this is not json");

        // Assert
        result.Success.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_Recipient_Is_Empty()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(string.Empty, "Hello"), ConfigWithToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_When_GetOa_Error_Is_Zero()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"error\":0,\"message\":\"Success\",\"data\":{\"name\":\"Klacks OA\"}}");

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestUri.ShouldBe(GetOaUrl);
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Get);
        _handler.LastAccessTokenHeader.ShouldBe(AccessToken);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_When_GetOa_Error_Is_Not_Zero()
    {
        // Arrange
        _handler.Response = JsonResponse("{\"error\":-216,\"message\":\"Access token is invalid\"}");

        // Act
        var result = await _sut.ValidateConfigAsync(ConfigWithToken);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_Without_Http_Call_When_AccessToken_Missing()
    {
        // Act
        var result = await _sut.ValidateConfigAsync("{}");

        // Assert
        result.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public void ValidateWebhook_Returns_Invalid_Always()
    {
        // Arrange
        var context = new WebhookValidationContext(
            "{\"event_name\":\"user_send_text\"}",
            new Dictionary<string, string>(),
            ConfigWithToken,
            string.Empty);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ParseWebhookPayload_Returns_Null_Always()
    {
        // Act
        var result = _sut.ParseWebhookPayload("{\"event_name\":\"user_send_text\",\"message\":{\"text\":\"Hi\"}}");

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

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private const string AccessTokenHeaderName = "access_token";

        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public int CallCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }
        public string? LastAccessTokenHeader { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestMethod = request.Method;
            LastAccessTokenHeader = request.Headers.TryGetValues(AccessTokenHeaderName, out var values)
                ? string.Join(",", values)
                : null;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response;
        }
    }
}
