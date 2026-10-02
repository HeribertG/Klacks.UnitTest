// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// RateLimitRetryHandler writes its throttling lines at Warning/Error, so they reach production logs.
/// Telegram carries the bot token in the path and WeChat its secret in the query: neither may appear.
/// </summary>

using System.Net;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Infrastructure.Http;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class RateLimitRetryHandlerCredentialLoggingTests
{
    private const string BotToken = "123456:SENTINEL-BOT-TOKEN";
    private const string WeChatSecret = "SENTINEL-WECHAT-SECRET";

    [TestCase("https://api.telegram.org/bot" + BotToken + "/sendMessage", "api.telegram.org")]
    [TestCase("https://api.weixin.qq.com/cgi-bin/token?grant_type=client_credential&appid=wx1&secret=" + WeChatSecret, "api.weixin.qq.com")]
    public async Task PersistentThrottling_LogsTheHostButNoCredential(string uri, string host)
    {
        var logger = new RecordingLogger<RateLimitRetryHandler>();
        using var client = new HttpClient(new RateLimitRetryHandler(logger) { InnerHandler = new AlwaysThrottled(TimeSpan.Zero) });

        var response = await client.GetAsync(uri);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        logger.Entries.Count.ShouldBe(MessagingRateLimitConstants.MaxRetryAttempts + 1);
        logger.Entries.ShouldAllBe(e => e.Message.Contains(host));
        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain(BotToken);
            entry.Message.ShouldNotContain(WeChatSecret);
        }
    }

    [Test]
    public async Task AWaitBeyondTheMaximum_LogsTheHostButNoCredential()
    {
        var logger = new RecordingLogger<RateLimitRetryHandler>();
        var tooLong = TimeSpan.FromMilliseconds(MessagingRateLimitConstants.MaxBackoffMilliseconds * 2);
        using var client = new HttpClient(new RateLimitRetryHandler(logger) { InnerHandler = new AlwaysThrottled(tooLong) });

        await client.GetAsync("https://api.telegram.org/bot" + BotToken + "/sendMessage");

        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].Message.ShouldContain("api.telegram.org");
        logger.Entries[0].Message.ShouldNotContain(BotToken);
    }

    private sealed class AlwaysThrottled(TimeSpan retryAfter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return Task.FromResult(response);
        }
    }
}
