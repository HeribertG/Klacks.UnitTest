// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Test harness for skills that mutate through the own REST API. It wires the REAL KlacksSelfApiClient
/// to a recording HttpMessageHandler, so a test exercises the actual request building, header wiring
/// and error mapping instead of a hand-written stub that could drift from them. Register a canned
/// response per (method, route), run the skill, then assert against the recorded calls.
/// </summary>

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.SelfApi;

public sealed class FakeSelfApi : IDisposable
{
    public const string BaseAddress = "https://self.test/";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly RecordingHandler _handler;
    private readonly HttpClient _httpClient;

    public FakeSelfApi()
    {
        _handler = new RecordingHandler(this);
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri(BaseAddress) };
        Client = new KlacksSelfApiClient(_httpClient, NullLogger<KlacksSelfApiClient>.Instance);
    }

    public IKlacksSelfApiClient Client { get; }

    public List<SelfApiCall> Calls { get; } = [];

    public SelfApiCall SingleCall => Calls.Count == 1
        ? Calls[0]
        : throw new InvalidOperationException($"Expected exactly one self-call, recorded {Calls.Count}.");

    /// <summary>Answers the given route with 200 and the serialized body.</summary>
    public FakeSelfApi Respond(HttpMethod method, string route, object? body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[Key(method, route)] = () =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = JsonContent.Create(body, body.GetType(), options: SerializerOptions);
            }

            return response;
        };

        return this;
    }

    /// <summary>Answers with a ProblemDetails body, the shape the API's error middleware produces.</summary>
    public FakeSelfApi RespondWithProblem(HttpMethod method, string route, HttpStatusCode status, string detail)
    {
        _responses[Key(method, route)] = () => new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(new { title = "Request failed", status = (int)status, detail })
        };

        return this;
    }

    /// <summary>Answers with a 400 carrying FluentValidation's per-field messages.</summary>
    public FakeSelfApi RespondWithValidationErrors(HttpMethod method, string route, IDictionary<string, string[]> errors)
    {
        _responses[Key(method, route)] = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new
            {
                title = "One or more validation failures occurred.",
                status = 400,
                detail = "Please refer to the errors property for additional details.",
                errors
            })
        };

        return this;
    }

    /// <summary>Deserializes the recorded request body of the call at the given index.</summary>
    public T? BodyOf<T>(int index = 0)
    {
        var body = Calls[index].Body;
        return body is null ? default : JsonSerializer.Deserialize<T>(body, SerializerOptions);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    private static string Key(HttpMethod method, string route) => $"{method.Method} {route.Trim('/')}";

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var route = request.RequestUri!.AbsolutePath.Trim('/');
        return _responses.TryGetValue(Key(request.Method, route), out var factory)
            ? factory()
            : throw new InvalidOperationException(
                $"No canned response registered for {request.Method.Method} /{route}. " +
                $"Registered: {string.Join(", ", _responses.Keys)}");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly FakeSelfApi _owner;

        public RecordingHandler(FakeSelfApi owner)
        {
            _owner = owner;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            _owner.Calls.Add(new SelfApiCall(
                request.Method,
                request.RequestUri!.AbsolutePath.Trim('/'),
                body,
                Header(request, SelfApiHeaders.SkillName),
                request.Headers.Authorization?.Parameter,
                Header(request, SelfApiHeaders.CorrelationId)));

            return _owner.Answer(request);
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }
}
