// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for RateLimitRetryHandler covering retries on throttling statuses, the Retry-After
/// header in both delta and date form, the backoff ceiling, and preservation of method,
/// headers and body across a retried request.
/// </summary>
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Infrastructure.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class RateLimitRetryHandlerTests
{
    private const string RequestUri = "https://provider.example/send";
    private const string RequestBody = "{\"text\":\"hello\"}";
    private const string BearerToken = "secret-token";

    private RecordingHandler _inner = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public void Setup()
    {
        _inner = new RecordingHandler();
        var handler = new RateLimitRetryHandler(Substitute.For<ILogger<RateLimitRetryHandler>>())
        {
            InnerHandler = _inner
        };
        _httpClient = new HttpClient(handler);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _inner.Dispose();
    }

    [Test]
    public async Task SendAsync_Does_Not_Retry_A_Successful_Response()
    {
        _inner.Enqueue(HttpStatusCode.OK);

        var response = await _httpClient.SendAsync(BuildRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _inner.Attempts.ShouldBe(1);
    }

    [Test]
    public async Task SendAsync_Does_Not_Retry_A_Client_Error()
    {
        _inner.Enqueue(HttpStatusCode.BadRequest);

        var response = await _httpClient.SendAsync(BuildRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _inner.Attempts.ShouldBe(1);
    }

    [Test]
    public async Task SendAsync_Retries_After_TooManyRequests_And_Returns_The_Successful_Response()
    {
        _inner.Enqueue(HttpStatusCode.TooManyRequests, TimeSpan.Zero);
        _inner.Enqueue(HttpStatusCode.OK);

        var response = await _httpClient.SendAsync(BuildRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _inner.Attempts.ShouldBe(2);
    }

    [Test]
    public async Task SendAsync_Retries_After_ServiceUnavailable()
    {
        _inner.Enqueue(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero);
        _inner.Enqueue(HttpStatusCode.OK);

        var response = await _httpClient.SendAsync(BuildRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _inner.Attempts.ShouldBe(2);
    }

    [Test]
    public async Task SendAsync_Gives_Up_After_The_Configured_Maximum_Of_Retries()
    {
        for (var i = 0; i < MessagingRateLimitConstants.MaxRetryAttempts + 1; i++)
            _inner.Enqueue(HttpStatusCode.TooManyRequests, TimeSpan.Zero);

        var response = await _httpClient.SendAsync(BuildRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        _inner.Attempts.ShouldBe(MessagingRateLimitConstants.MaxRetryAttempts + 1);
    }

    [Test]
    public async Task SendAsync_Does_Not_Wait_When_RetryAfter_Exceeds_The_Allowed_Maximum()
    {
        var beyondCeiling = TimeSpan.FromMilliseconds(MessagingRateLimitConstants.MaxBackoffMilliseconds + 1000);
        _inner.Enqueue(HttpStatusCode.TooManyRequests, beyondCeiling);
        _inner.Enqueue(HttpStatusCode.OK);

        var response = await _httpClient.SendAsync(BuildRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        _inner.Attempts.ShouldBe(1);
    }

    [Test]
    public async Task SendAsync_Treats_A_RetryAfter_Date_In_The_Past_As_No_Wait()
    {
        _inner.EnqueueWithRetryAfterDate(HttpStatusCode.TooManyRequests, DateTimeOffset.UtcNow.AddSeconds(-30));
        _inner.Enqueue(HttpStatusCode.OK);

        var response = await _httpClient.SendAsync(BuildRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _inner.Attempts.ShouldBe(2);
    }

    [Test]
    public async Task SendAsync_Preserves_Method_Body_And_Headers_On_The_Retried_Request()
    {
        _inner.Enqueue(HttpStatusCode.TooManyRequests, TimeSpan.Zero);
        _inner.Enqueue(HttpStatusCode.OK);

        await _httpClient.SendAsync(BuildRequest());

        _inner.Attempts.ShouldBe(2);
        _inner.ObservedMethods[1].ShouldBe(HttpMethod.Post);
        _inner.ObservedUris[1].ShouldBe(RequestUri);
        _inner.ObservedBodies[1].ShouldBe(RequestBody);
        _inner.ObservedAuthorization[1].ShouldBe(BearerToken);
        _inner.ObservedContentTypes[1].ShouldBe("application/json");
    }

    [Test]
    public async Task SendAsync_Returns_A_Retried_Response_Whose_Body_Is_Still_Readable()
    {
        const string expectedBody = "{\"ok\":true,\"ts\":\"1712345678.000100\"}";
        _inner.Enqueue(HttpStatusCode.TooManyRequests, TimeSpan.Zero);
        _inner.EnqueueWithBody(HttpStatusCode.OK, expectedBody);

        var response = await _httpClient.SendAsync(BuildRequest());
        var body = await response.Content.ReadAsStringAsync();

        body.ShouldBe(expectedBody);
    }

    [Test]
    public async Task SendAsync_Retries_A_Request_Without_Body()
    {
        _inner.Enqueue(HttpStatusCode.TooManyRequests, TimeSpan.Zero);
        _inner.Enqueue(HttpStatusCode.OK);

        var request = new HttpRequestMessage(HttpMethod.Get, RequestUri);
        var response = await _httpClient.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _inner.Attempts.ShouldBe(2);
        _inner.ObservedBodies[1].ShouldBeNull();
    }

    private static HttpRequestMessage BuildRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RequestUri)
        {
            Content = new StringContent(RequestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        return request;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public int Attempts { get; private set; }

        public List<HttpMethod> ObservedMethods { get; } = new();

        public List<string?> ObservedUris { get; } = new();

        public List<string?> ObservedBodies { get; } = new();

        public List<string?> ObservedAuthorization { get; } = new();

        public List<string?> ObservedContentTypes { get; } = new();

        public void Enqueue(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
        {
            var response = new HttpResponseMessage(statusCode);
            if (retryAfter.HasValue)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);

            _responses.Enqueue(response);
        }

        public void EnqueueWithBody(HttpStatusCode statusCode, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        public void EnqueueWithRetryAfterDate(HttpStatusCode statusCode, DateTimeOffset date)
        {
            var response = new HttpResponseMessage(statusCode);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
            _responses.Enqueue(response);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            ObservedMethods.Add(request.Method);
            ObservedUris.Add(request.RequestUri?.ToString());
            ObservedAuthorization.Add(request.Headers.Authorization?.Parameter);
            ObservedContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            ObservedBodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.Count > 0)
                    _responses.Dequeue().Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
