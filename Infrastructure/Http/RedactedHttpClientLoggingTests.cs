// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// AddRedactedHttpClientLogging must replace the IHttpClientFactory request logging for every client:
/// the framework logger hides only the query, so a Telegram bot token in the path reached the log at
/// Information level. The test goes through the real registration and a real factory client.
/// </summary>

using System.Collections.Concurrent;
using System.Net;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Http;

[TestFixture]
public class RedactedHttpClientLoggingTests
{
    private const string BotToken = "123456:SENTINEL-BOT-TOKEN";
    private const string QuerySecret = "SENTINEL-QUERY-SECRET";
    private const string ClientName = "telegram-like";
    private const string FrameworkHttpClientCategoryPrefix = "System.Net.Http.HttpClient";

    [Test]
    public async Task ASuccessfulRequest_LogsNoCredentialAndNoFrameworkLine()
    {
        var (factory, logs) = Build(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await factory.CreateClient(ClientName).GetAsync($"https://api.telegram.org/bot{BotToken}/sendMessage?x={QuerySecret}");

        logs.Entries.ShouldNotBeEmpty();
        logs.Entries.ShouldContain(e => e.Message.Contains("https://api.telegram.org/bot***/sendMessage?*"));
        AssertNoSecret(logs);
        logs.Entries.ShouldNotContain(e => e.Category.StartsWith(FrameworkHttpClientCategoryPrefix, StringComparison.Ordinal));
    }

    [Test]
    public async Task AFailedRequest_LogsNoCredential()
    {
        var (factory, logs) = Build(new StubHandler(request =>
            throw new HttpRequestException($"Connection to {request.RequestUri} refused")));

        var client = factory.CreateClient(ClientName);
        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync($"https://api.telegram.org/bot{BotToken}/getMe?x={QuerySecret}"));

        exception.ShouldNotBeNull();
        logs.Entries.ShouldContain(e => e.Message.Contains("failed"));
        AssertNoSecret(logs);
    }

    [Test]
    public async Task ATypedClient_IsCoveredToo()
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddRedactedHttpClientLogging();
        services.AddHttpClient<TypedClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<TypedClient>().Http.GetAsync($"https://api.telegram.org/bot{BotToken}/getMe");

        logs.Entries.ShouldNotBeEmpty();
        AssertNoSecret(logs);
        logs.Entries.ShouldNotContain(e => e.Category.StartsWith(FrameworkHttpClientCategoryPrefix, StringComparison.Ordinal));
    }

    private static (IHttpClientFactory Factory, CapturingLoggerProvider Logs) Build(HttpMessageHandler primary)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddRedactedHttpClientLogging();
        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => primary);
        return (services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), logs);
    }

    private static void AssertNoSecret(CapturingLoggerProvider logs)
    {
        foreach (var entry in logs.Entries)
        {
            entry.Message.ShouldNotContain(BotToken);
            entry.Message.ShouldNotContain(QuerySecret);
            (entry.Exception?.ToString() ?? string.Empty).ShouldNotContain(BotToken);
        }
    }

    public sealed class TypedClient(HttpClient http)
    {
        public HttpClient Http { get; } = http;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed record CapturedEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedEntry> _entries = new();

        public IReadOnlyCollection<CapturedEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new CapturedEntry(category, logLevel, formatter(state, exception), exception));
        }
    }
}
