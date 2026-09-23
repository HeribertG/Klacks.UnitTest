// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WhatsAppMessagingProvider's ICredentialDiagnoser implementation: valid/rejected/
/// unreachable/missing-token reason codes and that an access token never leaks into the diagnosis
/// result.
/// </summary>
using System.Net;
using System.Text;
using System.Text.Json;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class WhatsAppCredentialDiagnosisTests
{
    private const string SecretToken = "SECRET-123";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private WhatsAppMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _sut = new WhatsAppMessagingProvider(_httpClient, Substitute.For<ILogger<WhatsAppMessagingProvider>>());
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    private static string ValidConfig => $"{{\"AccessToken\":\"{SecretToken}\",\"PhoneNumberId\":\"123\"}}";

    [Test]
    public async Task DiagnoseCredentialsAsync_Valid_ReturnsDisplayPhoneNumberFact()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"id\":\"123\",\"display_phone_number\":\"+41 79 000 00 00\"}");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeTrue();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Valid);
        result.Facts![MessagingSetupConstants.FactDisplayPhoneNumber].ShouldBe("+41 79 000 00 00");
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Rejected_ReturnsVendorMessage()
    {
        _handler.Response = JsonResponse(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Invalid OAuth access token\"}}");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Rejected);
        result.VendorMessage.ShouldBe("Invalid OAuth access token");
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Unreachable_OnHttpRequestException()
    {
        _handler.Exception = new HttpRequestException("connection refused");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

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
    public async Task DiagnoseCredentialsAsync_MissingPhoneNumberId_MakesNoHttpCall()
    {
        var result = await _sut.DiagnoseCredentialsAsync($"{{\"AccessToken\":\"{SecretToken}\"}}");

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.MissingToken);
        _handler.CallCount.ShouldBe(0);
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task DiagnoseCredentialsAsync_ServerErrorOrRateLimit_IsUnreachableNotRejected(HttpStatusCode statusCode)
    {
        _handler.Response = new HttpResponseMessage(statusCode) { Content = new StringContent("<html>upstream error</html>") };

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Unreachable);
    }

    [TestCase(190, "Error validating access token: Session has expired")]
    [TestCase(100, "Unsupported get request. Object with ID '123' does not exist")]
    public async Task DiagnoseCredentialsAsync_GraphBadRequestWithCredentialErrorCode_IsRejected(int errorCode, string message)
    {
        _handler.Response = JsonResponse(HttpStatusCode.BadRequest, $"{{\"error\":{{\"message\":\"{message}\",\"type\":\"OAuthException\",\"code\":{errorCode}}}}}");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Rejected);
        result.VendorMessage.ShouldBe(message);
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_GraphBadRequestWithRateLimitCode_IsUnreachable()
    {
        _handler.Response = JsonResponse(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Application request limit reached\",\"code\":4}}");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Unreachable);
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_NeverLeaksToken()
    {
        _handler.Response = JsonResponse(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Invalid OAuth access token\"}}");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        JsonSerializer.Serialize(result).ShouldNotContain(SecretToken);
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
