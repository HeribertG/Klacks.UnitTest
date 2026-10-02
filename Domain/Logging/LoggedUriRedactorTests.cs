// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// LoggedUriRedactor decides what of a request URI may reach a log: never the query string (Gemini
/// "key=", WeChat "access_token="/"secret="), never a Telegram bot token in the path, never userinfo.
/// </summary>

using Klacks.Api.Domain.Logging;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Logging;

[TestFixture]
public class LoggedUriRedactorTests
{
    private const string Secret = "SENTINEL-SECRET-4711";

    [Test]
    public void QueryString_IsReplacedByTheFrameworkPlaceholder()
    {
        var logged = $"https://generativelanguage.googleapis.com/v1beta/models/m:generateContent?key={Secret}".RedactUriForLog();

        logged.ShouldBe("https://generativelanguage.googleapis.com/v1beta/models/m:generateContent?*");
    }

    [Test]
    public void TelegramBotTokenInThePath_IsMasked()
    {
        var logged = $"https://api.telegram.org/bot123456:{Secret}/sendMessage".RedactUriForLog();

        logged.ShouldBe("https://api.telegram.org/bot***/sendMessage");
    }

    [Test]
    public void TelegramBotTokenAndQuery_AreBothMasked()
    {
        var logged = new Uri($"https://api.telegram.org/bot123456:{Secret}/getUpdates?offset={Secret}").RedactUriForLog();

        logged.ShouldBe("https://api.telegram.org/bot***/getUpdates?*");
    }

    [Test]
    public void UserInfo_IsMasked()
    {
        var logged = $"https://user:{Secret}@example.org/path".RedactUriForLog();

        logged.ShouldBe("https://***@example.org/path");
    }

    [Test]
    public void ARelativeEndpointWithoutCredentials_IsUnchanged()
    {
        "chat/completions".RedactUriForLog().ShouldBe("chat/completions");
    }

    [Test]
    public void AnOrdinaryPathContainingBot_IsUnchanged()
    {
        "https://api.example.org/robots/bot-status".RedactUriForLog().ShouldBe("https://api.example.org/robots/bot-status");
    }

    [Test]
    public void LineBreaks_AreStripped()
    {
        "https://example.org/a\r\nforged".RedactUriForLog().ShouldBe("https://example.org/aforged");
    }

    [Test]
    public void Null_BecomesEmpty()
    {
        ((string?)null).RedactUriForLog().ShouldBeEmpty();
        ((Uri?)null).RedactUriForLog().ShouldBeEmpty();
    }
}
