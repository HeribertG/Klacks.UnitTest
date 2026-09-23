// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SlackMessagingProvider's ICredentialDiagnoser implementation: valid/rejected/unreachable/
/// missing-token reason codes, the chat:write scope check against the x-oauth-scopes response header,
/// and that a bot token never leaks into the diagnosis result.
/// </summary>
using System.Net;
using System.Text;
using System.Text.Json;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class SlackCredentialDiagnosisTests
{
    private const string SecretToken = "SECRET-123";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private SlackMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _sut = new SlackMessagingProvider(_httpClient, Substitute.For<ILogger<SlackMessagingProvider>>());
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Valid_WithChatWriteScope_ReturnsTeamAndBotUserFacts()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"team\":\"Klacks\",\"user\":\"klacksy\"}");
        _handler.Response.Headers.Add("x-oauth-scopes", "channels:history,chat:write");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeTrue();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Valid);
        result.Facts![MessagingSetupConstants.FactTeam].ShouldBe("Klacks");
        result.Facts![MessagingSetupConstants.FactBotUser].ShouldBe("klacksy");
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Valid_WhenScopesHeaderAbsent_DoesNotGuess()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"team\":\"Klacks\",\"user\":\"klacksy\"}");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeTrue();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Valid);
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_MissingScope_WhenHeaderLacksChatWrite()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"team\":\"Klacks\",\"user\":\"klacksy\"}");
        _handler.Response.Headers.Add("x-oauth-scopes", "channels:history");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.MissingScope);
        result.Facts![MessagingSetupConstants.FactMissingScope].ShouldBe("chat:write");
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Rejected_ReturnsVendorMessage()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"invalid_auth\"}");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Rejected);
        result.VendorMessage.ShouldBe("invalid_auth");
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

    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public async Task DiagnoseCredentialsAsync_RateLimitOrServerError_IsUnreachableNotRejected(HttpStatusCode statusCode)
    {
        _handler.Response = new HttpResponseMessage(statusCode) { Content = new StringContent("<html>Service Unavailable</html>") };

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Unreachable);
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Forbidden_IsRejected()
    {
        _handler.Response = JsonResponse(HttpStatusCode.Forbidden, "{\"ok\":false,\"error\":\"not_allowed_token_type\"}");

        var result = await _sut.DiagnoseCredentialsAsync($"{{\"BotToken\":\"{SecretToken}\"}}");

        result.ReasonCode.ShouldBe(CredentialReasonCodes.Rejected);
    }

    /// <summary>
    /// The vendor echoes the token in its error text. The adapter passes vendor text through, so the
    /// guarantee lives in the service: the report forwarded to the LLM must never contain the token.
    /// ChannelId keeps the provider in polling mode, so the credential check actually reaches the vendor.
    /// </summary>
    [Test]
    public async Task DiagnoseAsync_VendorEchoesToken_ReportNeverContainsIt()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, $"{{\"ok\":false,\"error\":\"invalid_auth for {SecretToken}\"}}");

        var report = await SetupDiagnosisHarness.DiagnoseSingleAsync(_sut, $"{{\"BotToken\":\"{SecretToken}\",\"ChannelId\":\"C123\"}}");

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
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (Exception != null)
                throw Exception;

            return Task.FromResult(Response);
        }
    }
}
