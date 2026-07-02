// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for TeamsMessagingProvider covering Adaptive Card sending via workflow webhooks,
/// config validation and the disabled incoming channel.
/// </summary>
using System.Net;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class TeamsMessagingProviderTests
{
    private const string WebhookUrl = "https://prod-42.westeurope.logic.azure.com/workflows/abc123/triggers/manual/paths/invoke";
    private const string ValidConfigJson = "{\"WebhookUrl\":\"" + WebhookUrl + "\"}";
    private const string AdaptiveCardContentType = "application/vnd.microsoft.card.adaptive";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<TeamsMessagingProvider> _logger = null!;
    private TeamsMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<TeamsMessagingProvider>>();
        _sut = new TeamsMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public void ProviderType_Is_MicrosoftTeams_And_Does_Not_Support_Phone_Recipients()
    {
        _sut.ProviderType.ShouldBe(MessagingConstants.ProviderTeams);
        _sut.SupportsPhoneAsRecipient.ShouldBeFalse();
    }

    [Test]
    public async Task SendAsync_Posts_AdaptiveCard_To_WebhookUrl_And_Succeeds_On_Accepted()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.Accepted);
        var request = new SendMessageRequest("ignored-recipient", "Shift plan was updated");

        // Act
        var result = await _sut.SendAsync(request, ValidConfigJson);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBeNull();
        _handler.CallCount.ShouldBe(1);
        _handler.LastRequestUri.ShouldNotBeNull();
        _handler.LastRequestUri!.ToString().ShouldBe(WebhookUrl);
        _handler.LastRequestBody.ShouldNotBeNull();
        _handler.LastRequestBody!.ShouldContain(AdaptiveCardContentType);
        _handler.LastRequestBody!.ShouldContain("Shift plan was updated");
    }

    [Test]
    public async Task SendAsync_Returns_Failure_Without_HttpCall_On_Empty_WebhookUrl()
    {
        // Arrange
        var request = new SendMessageRequest("ignored-recipient", "Hello");

        // Act
        var result = await _sut.SendAsync(request, "{}");

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        _handler.CallCount.ShouldBe(0);
    }

    [TestCase("{\"WebhookUrl\":\"http://prod-42.westeurope.logic.azure.com/workflows/abc\"}")]
    [TestCase("{\"WebhookUrl\":\"workflows/abc/triggers/manual\"}")]
    public async Task SendAsync_Returns_Failure_Without_HttpCall_On_Invalid_WebhookUrl(string configJson)
    {
        // Arrange
        var request = new SendMessageRequest("ignored-recipient", "Hello");

        // Act
        var result = await _sut.SendAsync(request, configJson);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        _handler.CallCount.ShouldBe(0);
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task SendAsync_Returns_Failure_With_StatusCode_On_Error_Response(HttpStatusCode statusCode)
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(statusCode);
        var request = new SendMessageRequest("ignored-recipient", "Hello");

        // Act
        var result = await _sut.SendAsync(request, ValidConfigJson);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain(statusCode.ToString());
        _handler.CallCount.ShouldBe(1);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_True_When_Test_Message_Is_Accepted()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.Accepted);

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfigJson);

        // Assert
        result.ShouldBeTrue();
        _handler.CallCount.ShouldBe(1);
        _handler.LastRequestUri!.ToString().ShouldBe(WebhookUrl);
        _handler.LastRequestBody!.ShouldContain(AdaptiveCardContentType);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_On_Error_Response()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfigJson);

        // Assert
        result.ShouldBeFalse();
        _handler.CallCount.ShouldBe(1);
    }

    [Test]
    public async Task ValidateConfigAsync_Returns_False_Without_HttpCall_On_Empty_WebhookUrl()
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
            "{\"any\":\"payload\"}",
            new Dictionary<string, string>(),
            ValidConfigJson,
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
        var result = _sut.ParseWebhookPayload("{\"any\":\"payload\"}");

        // Assert
        result.ShouldBeNull();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.Accepted);
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response;
        }
    }
}
