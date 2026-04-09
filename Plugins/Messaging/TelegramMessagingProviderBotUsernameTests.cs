// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for TelegramMessagingProvider.GetBotUsernameAsync including caching and invalid config handling.
/// </summary>
using System.Net;
using System.Text;
using FluentAssertions;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class TelegramMessagingProviderBotUsernameTests
{
    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private IMemoryCache _cache = null!;
    private ILogger<TelegramMessagingProvider> _logger = null!;
    private TelegramMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _logger = Substitute.For<ILogger<TelegramMessagingProvider>>();
        _sut = new TelegramMessagingProvider(_httpClient, _cache, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
        _cache.Dispose();
    }

    [Test]
    public async Task GetBotUsernameAsync_Returns_Username_From_GetMe()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"ok\":true,\"result\":{\"id\":1,\"username\":\"klacks_bot\"}}",
                Encoding.UTF8,
                "application/json")
        };

        var result = await _sut.GetBotUsernameAsync("{\"BotToken\":\"abc\"}");

        result.Should().Be("klacks_bot");
    }

    [Test]
    public async Task GetBotUsernameAsync_Returns_Cached_On_Second_Call()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"ok\":true,\"result\":{\"id\":1,\"username\":\"klacks_bot\"}}",
                Encoding.UTF8,
                "application/json")
        };

        await _sut.GetBotUsernameAsync("{\"BotToken\":\"abc\"}");
        _handler.CallCount.Should().Be(1);

        await _sut.GetBotUsernameAsync("{\"BotToken\":\"abc\"}");
        _handler.CallCount.Should().Be(1);
    }

    [Test]
    public async Task GetBotUsernameAsync_Returns_Null_On_Invalid_Config()
    {
        var result = await _sut.GetBotUsernameAsync("{}");
        result.Should().BeNull();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Response);
        }
    }
}
