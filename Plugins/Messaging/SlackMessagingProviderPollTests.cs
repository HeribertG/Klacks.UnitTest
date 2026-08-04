// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SlackMessagingProvider.PollAsync, the inbound path used when no publicly reachable
/// webhook URL exists. Covers the first-round anchor, the bot/subtype echo guard, cursor
/// advancement and the ordering the downstream bridge relies on.
/// </summary>
using System.Net;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SlackMessagingProviderPollTests
{
    private const string PollConfig = """{"BotToken":"xoxb-test-token","ChannelId":"C0TEST123"}""";
    private const string Cursor = "1785854000.000000";

    private HttpClient _httpClient = null!;
    private RecordingHandler _handler = null!;
    private SlackMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new RecordingHandler();
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
    public async Task PollAsync_FirstRound_AnchorsAtNowWithoutCallingSlack()
    {
        var result = await _sut.PollAsync(PollConfig, null);

        // Replaying the whole channel history on first activation would feed every past message
        // through the assistant as if it had just arrived.
        result.Messages.ShouldBeEmpty();
        result.NextCursor.ShouldNotBeNullOrWhiteSpace();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task PollAsync_WithoutChannelId_DoesNothing()
    {
        // conversations.history only accepts an ID; a '#name' cannot be resolved without channels:read.
        var result = await _sut.PollAsync("""{"BotToken":"xoxb-test-token","DefaultChannel":"#general"}""", Cursor);

        result.Messages.ShouldBeEmpty();
        result.NextCursor.ShouldBe(Cursor);
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task PollAsync_ReturnsUserMessagesOldestFirstAndAdvancesCursor()
    {
        _handler.SetJson("""
        {"ok":true,"messages":[
          {"ts":"1785854300.000200","user":"U1","text":"zweite"},
          {"ts":"1785854100.000100","user":"U1","text":"erste"}
        ]}
        """);

        var result = await _sut.PollAsync(PollConfig, Cursor);

        result.Messages.Count.ShouldBe(2);
        result.Messages[0].Content.ShouldBe("erste");
        result.Messages[1].Content.ShouldBe("zweite");
        result.NextCursor.ShouldBe("1785854300.000200");
        _handler.LastRequestUri.ShouldContain("channel=C0TEST123");
        _handler.LastRequestUri.ShouldContain("oldest=");
        _handler.LastAuthorizationHeader.ShouldBe("Bearer xoxb-test-token");
    }

    [Test]
    public async Task PollAsync_SkipsOwnBotMessagesButStillAdvancesPastThem()
    {
        _handler.SetJson("""
        {"ok":true,"messages":[
          {"ts":"1785854300.000200","user":"UBOT","bot_id":"B123","text":"meine eigene Antwort"}
        ]}
        """);

        var result = await _sut.PollAsync(PollConfig, Cursor);

        // Without this guard the assistant's own reply comes back as a new inbound message and
        // triggers another turn - one reply per poll interval, forever.
        result.Messages.ShouldBeEmpty();
        result.NextCursor.ShouldBe("1785854300.000200");
    }

    [Test]
    public async Task PollAsync_SkipsMessagesCarryingASubtype()
    {
        _handler.SetJson("""
        {"ok":true,"messages":[
          {"ts":"1785854300.000200","user":"U1","subtype":"channel_join","text":"hat den Kanal betreten"}
        ]}
        """);

        var result = await _sut.PollAsync(PollConfig, Cursor);

        result.Messages.ShouldBeEmpty();
    }

    [Test]
    public async Task PollAsync_SlackReportsNotOk_KeepsCursorSoNothingIsSkipped()
    {
        _handler.SetJson("""{"ok":false,"error":"channel_not_found"}""");

        var result = await _sut.PollAsync(PollConfig, Cursor);

        result.Messages.ShouldBeEmpty();
        result.NextCursor.ShouldBe(Cursor);
    }

    [Test]
    public async Task PollAsync_HttpError_KeepsCursorSoNothingIsSkipped()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var result = await _sut.PollAsync(PollConfig, Cursor);

        result.Messages.ShouldBeEmpty();
        result.NextCursor.ShouldBe(Cursor);
    }

    [Test]
    public async Task PollAsync_MessageWithoutText_IsIgnored()
    {
        _handler.SetJson("""
        {"ok":true,"messages":[{"ts":"1785854300.000200","user":"U1"}]}
        """);

        var result = await _sut.PollAsync(PollConfig, Cursor);

        result.Messages.ShouldBeEmpty();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public int CallCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }

        public void SetJson(string json)
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            return Task.FromResult(Response);
        }
    }
}
