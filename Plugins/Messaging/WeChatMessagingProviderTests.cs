// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WeChatMessagingProvider covering customer service message sending,
/// access token caching and invalidation, config validation and webhook stubs.
/// </summary>
using System.Net;
using System.Text;
using System.Text.Json;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class WeChatMessagingProviderTests
{
    private const string AppId = "wx1234567890abcdef";
    private const string AppSecret = "secret-value";
    private const string ValidConfig = "{\"AppId\":\"wx1234567890abcdef\",\"AppSecret\":\"secret-value\"}";
    private const string ConfigMissingSecret = "{\"AppId\":\"wx1234567890abcdef\"}";
    private const string TokenEndpointPrefix = "https://api.weixin.qq.com/cgi-bin/token";
    private const string SendEndpointPrefix = "https://api.weixin.qq.com/cgi-bin/message/custom/send";
    private const string TokenOkResponse = "{\"access_token\":\"TOKEN_A\",\"expires_in\":7200}";
    private const string SecondTokenOkResponse = "{\"access_token\":\"TOKEN_B\",\"expires_in\":7200}";
    private const string TokenErrorResponse = "{\"errcode\":40013,\"errmsg\":\"invalid appid\"}";
    private const string SendOkResponse = "{\"errcode\":0,\"errmsg\":\"ok\"}";
    private const string SendOutOfTimeLimitResponse = "{\"errcode\":45015,\"errmsg\":\"response out of time limit\"}";
    private const string SendInvalidCredentialResponse = "{\"errcode\":40001,\"errmsg\":\"invalid credential\"}";
    private const string Recipient = "openid-123";
    private const string MessageContent = "Hello WeChat";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private IMemoryCache _cache = null!;
    private ILogger<WeChatMessagingProvider> _logger = null!;
    private WeChatMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _logger = Substitute.For<ILogger<WeChatMessagingProvider>>();
        _sut = new WeChatMessagingProvider(_httpClient, _cache, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
        _cache.Dispose();
    }

    [Test]
    public async Task SendAsync_Fetches_Token_Then_Sends_Message_With_Recipient_And_Content()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenOkResponse);
        _handler.EnqueueJsonResponse(SendOkResponse);

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);

        // Assert
        result.Success.ShouldBeTrue();
        _handler.CallCount.ShouldBe(2);
        _handler.RequestUris[0].ShouldStartWith(TokenEndpointPrefix);
        _handler.RequestUris[0].ShouldContain($"appid={AppId}");
        _handler.RequestUris[0].ShouldContain($"secret={AppSecret}");
        _handler.RequestUris[1].ShouldStartWith(SendEndpointPrefix);
        _handler.RequestUris[1].ShouldContain("access_token=TOKEN_A");
        var payload = JsonSerializer.Deserialize<JsonElement>(_handler.RequestBodies[1]!);
        payload.GetProperty("touser").GetString().ShouldBe(Recipient);
        payload.GetProperty("msgtype").GetString().ShouldBe("text");
        payload.GetProperty("text").GetProperty("content").GetString().ShouldBe(MessageContent);
    }

    [Test]
    public async Task SendAsync_Returns_Failure_With_Errmsg_When_48h_Window_Expired()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenOkResponse);
        _handler.EnqueueJsonResponse(SendOutOfTimeLimitResponse);

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("45015");
        result.ErrorMessage.ShouldContain("response out of time limit");
    }

    [Test]
    public async Task SendAsync_Reuses_Cached_Token_On_Second_Call()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenOkResponse);
        _handler.EnqueueJsonResponse(SendOkResponse);
        _handler.EnqueueJsonResponse(SendOkResponse);

        // Act
        var first = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);
        var second = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);

        // Assert
        first.Success.ShouldBeTrue();
        second.Success.ShouldBeTrue();
        _handler.CallCount.ShouldBe(3);
        _handler.RequestUris.Count(uri => uri.StartsWith(TokenEndpointPrefix)).ShouldBe(1);
        _handler.RequestUris[2].ShouldContain("access_token=TOKEN_A");
    }

    [Test]
    public async Task SendAsync_Removes_Cached_Token_On_Errcode_40001_So_Next_Send_Fetches_Fresh_Token()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenOkResponse);
        _handler.EnqueueJsonResponse(SendOkResponse);
        _handler.EnqueueJsonResponse(SendInvalidCredentialResponse);
        _handler.EnqueueJsonResponse(SecondTokenOkResponse);
        _handler.EnqueueJsonResponse(SendOkResponse);

        // Act
        var first = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);
        var second = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);
        var third = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);

        // Assert
        first.Success.ShouldBeTrue();
        second.Success.ShouldBeFalse();
        second.ErrorMessage!.ShouldContain("40001");
        third.Success.ShouldBeTrue();
        _handler.CallCount.ShouldBe(5);
        _handler.RequestUris.Count(uri => uri.StartsWith(TokenEndpointPrefix)).ShouldBe(2);
        _handler.RequestUris[4].ShouldContain("access_token=TOKEN_B");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Send_Call_When_Token_Request_Fails()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenErrorResponse);

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        _handler.CallCount.ShouldBe(1);
        _handler.RequestUris[0].ShouldStartWith(TokenEndpointPrefix);
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_Config_Is_Empty()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), "{}");

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_AppSecret_Missing()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ConfigMissingSecret);

        // Assert
        result.Success.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_Http_Call_When_Config_Is_Not_Json()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), "this is not json");

        // Assert
        result.Success.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_When_Token_Request_Succeeds()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenOkResponse);

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeTrue();
        _handler.CallCount.ShouldBe(1);
        _handler.RequestUris[0].ShouldStartWith(TokenEndpointPrefix);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_When_Token_Request_Returns_Errcode()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenErrorResponse);

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_Without_Http_Call_When_Credentials_Missing()
    {
        // Act
        var result = await _sut.ValidateConfigAsync(ConfigMissingSecret);

        // Assert
        result.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_Fetches_Fresh_Token_Even_When_Token_Is_Cached()
    {
        // Arrange
        _handler.EnqueueJsonResponse(TokenOkResponse);
        _handler.EnqueueJsonResponse(SendOkResponse);
        _handler.EnqueueJsonResponse(SecondTokenOkResponse);

        // Act
        await _sut.SendAsync(new SendMessageRequest(Recipient, MessageContent), ValidConfig);
        var result = await _sut.ValidateConfigAsync(ValidConfig);

        // Assert
        result.ShouldBeTrue();
        _handler.RequestUris.Count(uri => uri.StartsWith(TokenEndpointPrefix)).ShouldBe(2);
    }

    [Test]
    public void ValidateWebhook_Always_Returns_Invalid()
    {
        // Arrange
        var context = new WebhookValidationContext("{}", new Dictionary<string, string>(), ValidConfig, string.Empty);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ParseWebhookPayload_Always_Returns_Null()
    {
        // Act
        var result = _sut.ParseWebhookPayload("<xml><Content>hello</Content></xml>");

        // Assert
        result.ShouldBeNull();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<string> RequestUris { get; } = new();
        public List<string?> RequestBodies { get; } = new();
        public int CallCount => RequestUris.Count;

        public void EnqueueJsonResponse(string json)
        {
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            RequestBodies.Add(request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
