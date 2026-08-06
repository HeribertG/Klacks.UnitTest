// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the assistant audit line. Routing skill mutations through HTTP only pays for itself
/// if the write is attributable, so a request carrying X-Klacksy-Skill must be logged with the skill,
/// the conversation and the acting user — and ordinary browser traffic must produce no line at all.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Middleware;

[TestFixture]
public class SkillRequestLoggingMiddlewareTests
{
    private sealed class CapturingLogger : ILogger<SkillRequestLoggingMiddleware>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static DefaultHttpContext Context(string? skillName, string? correlationId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/backend/Expenses";

        if (skillName is not null)
        {
            context.Request.Headers[SelfApiHeaders.SkillName] = skillName;
        }

        if (correlationId is not null)
        {
            context.Request.Headers[SelfApiHeaders.CorrelationId] = correlationId;
        }

        return context;
    }

    [Test]
    public async Task Request_WithSkillHeader_IsLoggedWithSkillAndConversation()
    {
        var logger = new CapturingLogger();
        var context = Context("add_expense", "conversation-7");
        var middleware = new SkillRequestLoggingMiddleware(
            ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, logger);

        await middleware.InvokeAsync(context);

        logger.Messages.Count.ShouldBe(1);
        logger.Messages[0].ShouldContain("add_expense");
        logger.Messages[0].ShouldContain("conversation-7");
        logger.Messages[0].ShouldContain("/api/backend/Expenses");
        logger.Messages[0].ShouldContain("200");
    }

    [Test]
    public async Task Request_WithoutSkillHeader_IsNotLogged()
    {
        var logger = new CapturingLogger();
        var middleware = new SkillRequestLoggingMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(Context(skillName: null));

        logger.Messages.ShouldBeEmpty();
    }

    [Test]
    public async Task RefusedRequest_IsLoggedWithItsStatus()
    {
        var logger = new CapturingLogger();
        var middleware = new SkillRequestLoggingMiddleware(
            ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; }, logger);

        await middleware.InvokeAsync(Context("delete_client"));

        logger.Messages.Count.ShouldBe(1);
        logger.Messages[0].ShouldContain("403");
        logger.Messages[0].ShouldContain("delete_client");
    }

    [Test]
    public async Task Request_AlwaysReachesTheRestOfThePipeline()
    {
        var called = 0;
        var middleware = new SkillRequestLoggingMiddleware(
            _ => { called++; return Task.CompletedTask; }, new CapturingLogger());

        await middleware.InvokeAsync(Context("add_expense"));
        await middleware.InvokeAsync(Context(skillName: null));

        called.ShouldBe(2);
    }
}
