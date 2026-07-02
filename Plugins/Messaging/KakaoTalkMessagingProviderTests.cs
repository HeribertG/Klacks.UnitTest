// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for KakaoTalkMessagingProvider covering friends default template send requests,
/// receiver UUID confirmation, failure info mapping, Kakao error responses,
/// config validation and webhook behavior.
/// </summary>
using System.Net;
using System.Text;
using System.Text.Json;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class KakaoTalkMessagingProviderTests
{
    private const string ValidConfig = "{\"AccessToken\":\"kakao-token-123\"}";
    private const string RecipientUuid = "friend-uuid-1";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<KakaoTalkMessagingProvider> _logger = null!;
    private KakaoTalkMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<KakaoTalkMessagingProvider>>();
        _sut = new KakaoTalkMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public void ProviderType_Is_KakaoTalk_And_Phone_Recipients_Are_Not_Supported()
    {
        // Assert
        _sut.ProviderType.ShouldBe(MessagingConstants.ProviderKakaoTalk);
        _sut.SupportsPhoneAsRecipient.ShouldBeFalse();
    }

    [Test]
    public async Task SendAsync_With_Valid_Config_Sends_Correct_Request_And_Returns_Success()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.OK,
            "{\"successful_receiver_uuids\":[\"friend-uuid-1\"]}");
        var request = new SendMessageRequest(RecipientUuid, "Hello Kakao");

        // Act
        var result = await _sut.SendAsync(request, ValidConfig);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBeNull();
        result.ErrorMessage.ShouldBeNull();
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        _handler.LastRequestUri.ShouldBe("https://kapi.kakao.com/v1/api/talk/friends/message/default/send");
        _handler.LastAuthorizationHeader.ShouldBe("Bearer kakao-token-123");

        var form = ParseFormBody(_handler.LastRequestBody!);
        var receiverUuids = JsonSerializer.Deserialize<string[]>(form["receiver_uuids"]);
        receiverUuids.ShouldNotBeNull();
        receiverUuids.ShouldBe(new[] { RecipientUuid });

        var templateObject = JsonSerializer.Deserialize<JsonElement>(form["template_object"]);
        templateObject.GetProperty("object_type").GetString().ShouldBe("text");
        templateObject.GetProperty("text").GetString().ShouldBe("Hello Kakao");
        templateObject.GetProperty("link").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Test]
    public async Task SendAsync_When_Response_Contains_FailureInfo_Returns_Failure_With_Detail()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.OK,
            "{\"successful_receiver_uuids\":[],\"failure_info\":[{\"code\":-532,\"msg\":\"daily message limit per sender exceeded\",\"receiver_uuids\":[\"friend-uuid-1\"]}]}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(RecipientUuid, "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ExternalMessageId.ShouldBeNull();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("daily message limit per sender exceeded");
        result.ErrorMessage.ShouldContain("-532");
    }

    [Test]
    public async Task SendAsync_When_Recipient_Missing_From_Successful_Uuids_Returns_Failure()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.OK,
            "{\"successful_receiver_uuids\":[\"other-uuid\"]}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(RecipientUuid, "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("successful_receiver_uuids");
    }

    [Test]
    public async Task SendAsync_On_Unauthorized_Returns_Failure_With_Kakao_Msg_And_Code()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.Unauthorized,
            "{\"msg\":\"this access token does not exist\",\"code\":-401}");

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(RecipientUuid, "Hello"), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("this access token does not exist");
        result.ErrorMessage.ShouldContain("-401");
        result.ErrorMessage.ShouldContain(HttpStatusCode.Unauthorized.ToString());
    }

    [TestCase("{}")]
    [TestCase("{\"AccessToken\":\"\"}")]
    [TestCase("{\"AccessToken\":\"   \"}")]
    [TestCase("not-json")]
    public async Task SendAsync_With_Missing_AccessToken_Fails_Without_Http_Call(string config)
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(RecipientUuid, "Hello"), config);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_On_Success_Response()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.OK,
            "{\"id\":123456789,\"expires_in\":21599,\"app_id\":987654}");

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Get);
        _handler.LastRequestUri.ShouldBe("https://kapi.kakao.com/v1/user/access_token_info");
        _handler.LastAuthorizationHeader.ShouldBe("Bearer kakao-token-123");
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_On_Unauthorized()
    {
        // Arrange
        _handler.Response = JsonResponse(
            HttpStatusCode.Unauthorized,
            "{\"msg\":\"this access token does not exist\",\"code\":-401}");

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task ValidateConfigAsync_With_Missing_AccessToken_Returns_False_Without_Http_Call()
    {
        // Act
        var result = await _sut.ValidateConfigAsync("{}");

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
        var result = _sut.ParseWebhookPayload("{\"message\":\"incoming\"}");

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
