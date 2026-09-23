// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for LineMessagingProvider's ICredentialDiagnoser implementation: valid/rejected/unreachable/
/// missing-token reason codes and that a channel access token never leaks into the diagnosis result.
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
public class LineCredentialDiagnosisTests
{
    private const string SecretToken = "SECRET-123";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private LineMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _sut = new LineMessagingProvider(_httpClient, Substitute.For<ILogger<LineMessagingProvider>>());
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    private static string ValidConfig => $"{{\"ChannelAccessToken\":\"{SecretToken}\"}}";

    [Test]
    public async Task DiagnoseCredentialsAsync_Valid_ReturnsBasicIdFact()
    {
        _handler.Response = JsonResponse(HttpStatusCode.OK, "{\"basicId\":\"@klacks\",\"displayName\":\"Klacks\"}");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeTrue();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Valid);
        result.Facts![MessagingSetupConstants.FactBasicId].ShouldBe("@klacks");
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_Rejected_OnUnauthorized()
    {
        _handler.Response = JsonResponse(HttpStatusCode.Unauthorized, "{\"message\":\"Authentication failed\"}");

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Rejected);
        result.VendorMessage.ShouldBe("Authentication failed");
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

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task DiagnoseCredentialsAsync_ServerErrorOrRateLimit_IsUnreachableNotRejected(HttpStatusCode statusCode)
    {
        _handler.Response = new HttpResponseMessage(statusCode) { Content = new StringContent("<html>upstream error</html>") };

        var result = await _sut.DiagnoseCredentialsAsync(ValidConfig);

        result.IsValid.ShouldBeFalse();
        result.ReasonCode.ShouldBe(CredentialReasonCodes.Unreachable);
    }

    [Test]
    public async Task DiagnoseCredentialsAsync_NeverLeaksToken()
    {
        _handler.Response = JsonResponse(HttpStatusCode.Unauthorized, "{\"message\":\"Authentication failed\"}");

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
