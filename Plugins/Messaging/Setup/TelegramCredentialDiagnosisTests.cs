// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for TelegramMessagingProvider's ICredentialDiagnoser and ITelegramWebhookInspector
/// implementation: reason codes for valid/rejected/unreachable/missing-token credentials,
/// getWebhookInfo parsing, and that a bot token never leaks into the diagnosis result.
/// </summary>
using System.Net;
using System.Text;
using System.Text.Json;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class TelegramCredentialDiagnosisTests
{
    private const string SecretToken = "SECRET-123";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private IMemoryCache _cache = null!;
    private TelegramMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _sut = new TelegramMessagingProvider(_httpClient, _cache, Substitute.For<ILogger<TelegramMessagingProvider>>());
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
        _cache.Dispose();
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Valid_ReturnsBotUsernameFact()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"id\":1,\"username\":\"klacks_bot\"}}");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeTrue();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Valid);
        result.Facts.ShouldNotBeNull();
        result.Facts![MessagingSetupConstants.FactBotUsername].ShouldBe("klacks_bot");
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Rejected_ReturnsVendorMessage()
    {
        _handler.Response = JsonResponse(HttpStatusCode.Unauthorized, "{\"ok\":false,\"description\":\"Unauthorized\"}");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Rejected);
        result.VendorMessage.ShouldBe("Unauthorized");
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Unreachable_OnHttpRequestException()
    {
        _handler.Exception = new HttpRequestException("connection refused");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Unreachable);
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_MissingToken_MakesNoHttpCall()
    {
        var result = await _sut.DiagnoseCredentialsAsync("{}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.MissingToken);
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task GetWebhookInfoAsync_ParsesUrlPendingCountAndUtcErrorDate()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK,
            "{\"ok\":true,\"result\":{\"url\":\"https://klacks.example.com/hook\",\"pending_update_count\":3," +
            "\"last_error_message\":\"Connection refused\",\"last_error_date\":1700000000}}");

        var info = await _sut.GetWebhookInfoAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        info.ShouldNotBeNull();
        info!.Url.ShouldBe("https://klacks.example.com/hook");
        info.PendingUpdateCount.ShouldBe(3);
        info.LastErrorMessage.ShouldBe("Connection refused");
        info.LastErrorAtUtc.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
    }

    [Test]
    public async Task GetWebhookInfoAsync_ReturnsNull_OnFailure()
    {
        _handler.Exception = new HttpRequestException("connection refused");

        var info = await _sut.GetWebhookInfoAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        info.ShouldBeNull();
    }

    [Test]
    public async Task GetWebhookInfoAsync_ReturnsNull_OnMissingToken()
    {
        var info = await _sut.GetWebhookInfoAsync("{}");

        info.ShouldBeNull();
        _handler.CallCount.ShouldBe(0);
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task DiagnoseCredentialsAsync_ServerErrorOrRateLimit_IsUnreachableNotRejected(HttpStatusCode statusCode)
    {
        _handler.Response = new HttpResponseMessage(statusCode) { Content = new StringContent("<html>Bad Gateway</html>") };

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Unreachable);
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_NotFound_MeansMalformedTokenAndIsRejected()
    {
        _handler.Response = JsonResponse(HttpStatusCode.NotFound, "{\"ok\":false,\"error_code\":404,\"description\":\"Not Found\"}");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.ReasonCode.ShouldBe(CredentialReasonCodes.Rejected);
        result.VendorMessage.ShouldBe("Not Found");
    }

    [Test]
    public async Task GetWebhookInfoAsync_CallerCancelled_PropagatesInsteadOfReturningNull()
    {
        using var cts = new CancellationTokenSource();
        _handler.CancelOnSend = cts;
        _handler.Exception = new TaskCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.GetWebhookInfoAsync($"{{\"BotToken\":\"{SecretToken}\"}}", cts.Token));
    }

    /// <summary>
    /// The vendor echoes the token in its error text. The adapter passes vendor text through, so the
    /// guarantee lives in the service: the report forwarded to the LLM must never contain the token.
    /// </summary>
    [Test]
    public async Task DiagnoseAsync_VendorEchoesToken_ReportNeverContainsIt()
    {
        _handler.Response = JsonResponse(HttpStatusCode.Unauthorized,
            $"{{\"ok\":false,\"description\":\"Unauthorized: bot {SecretToken} is not valid\"}}");

        var report = await SetupDiagnosisHarness.DiagnoseSingleAsync(_sut, $"{{\"BotToken\":\"{SecretToken}\"}}");

        var credentials = report.Steps.Single(step => step.Code == SetupStepCodes.Credentials);
        credentials.Status.ShouldBe(SetupStepStatus.Error);
        credentials.Detail!.ShouldStartWith(CredentialReasonCodes.Rejected);
        _handler.CallCount.ShouldBeGreaterThanOrEqualTo(1);
        var json = JsonSerializer.Serialize(report);
        json.ShouldNotContain(SecretToken);
        json.ShouldContain(SetupStepDetails.RedactedPlaceholder);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public Exception? Exception { get; set; }
        public CancellationTokenSource? CancelOnSend { get; set; }
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            CancelOnSend?.Cancel();
            if (Exception != null)
                throw Exception;

            return Task.FromResult(Response);
        }
    }
}
