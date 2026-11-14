// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.OAuth;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthRedirectMatcherTests
{
    private const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";
    private const string ChatGptPrefix = "https://chatgpt.com/connector/oauth/";
    private const string CustomTarget = "https://mcp.example.org/oauth/callback";

    private static readonly IReadOnlyCollection<string> NoCustomTargets = [];

    private static readonly string[] ForeignHostTricks =
    [
        "https://claude.ai.evil.example/api/mcp/auth_callback",
        "https://claude.ai@evil.example/api/mcp/auth_callback",
        "https://evil.example@claude.ai/api/mcp/auth_callback",
        "https://user:secret@claude.ai/api/mcp/auth_callback",
        "https://evil.example/claude.ai/api/mcp/auth_callback",
        "https://evil.example/?https://claude.ai/api/mcp/auth_callback",
        "https://notclaude.ai/api/mcp/auth_callback",
        "https://claude.ai.evil.example/connector/oauth/abc",
        "https://chatgpt.com.evil.example/connector/oauth/abc",
        "https://chatgpt.com@evil.example/connector/oauth/abc",
        "https://evil.example\\@claude.ai/api/mcp/auth_callback",
        "https://evil.example\\.claude.ai/api/mcp/auth_callback",
        "https://claude.ai\\@evil.example/api/mcp/auth_callback"
    ];

    private static readonly string[] SchemePortAndCaseTricks =
    [
        "http://claude.ai/api/mcp/auth_callback",
        "HTTPS://claude.ai/api/mcp/auth_callback",
        "https://CLAUDE.AI/api/mcp/auth_callback",
        "https://Claude.ai/api/mcp/auth_callback",
        "https://claude.ai/API/MCP/AUTH_CALLBACK",
        "https://claude.ai./api/mcp/auth_callback",
        "https://claude.ai:443/api/mcp/auth_callback",
        "https://claude.ai:8443/api/mcp/auth_callback",
        "https://claude.ai:/api/mcp/auth_callback",
        "ftp://claude.ai/api/mcp/auth_callback",
        "cursor://anysphere.cursor-retrieval/mcp/oauth/callback",
        "//claude.ai/api/mcp/auth_callback",
        "claude.ai/api/mcp/auth_callback",
        "https://cursor.com/agents/mcp/oauth/callback",
        "https://www.cursor.com/agents/mcp/oauth/callback"
    ];

    private static readonly string[] IdnAndEscapeTricks =
    [
        "https://clаude.ai/api/mcp/auth_callback",
        "https://xn--clude-9ve.ai/api/mcp/auth_callback",
        "https://claude.ai/api/mcp/%2e%2e/auth_callback",
        "https://claude.ai/api/mcp/%2E%2E/auth_callback",
        "https://claude.ai/api/mcp%2fauth_callback",
        "https://claude.ai/api/mcp/auth_callback%00",
        "https://claude.ai/api/mcp/auth_callback%20",
        "https://claude.ai/api/mcp/../mcp/auth_callback",
        "https://claude.ai/api/./mcp/auth_callback",
        "https://claude.ai//api/mcp/auth_callback",
        "https://claude.ai/api//mcp/auth_callback",
        "https://claude.ai\\api/mcp/auth_callback",
        "https://claude.ai/api\\mcp/auth_callback",
        "https://claude.ai/api/mcp/auth_callback/",
        "https://claude.ai/api/mcp/auth_callback?next=https://evil.example",
        "https://claude.ai/api/mcp/auth_callback#https://evil.example",
        "https://claude.ai/api/mcp/auth_callback\n",
        "https://claude.ai/api/mcp/auth_callback\r\nLocation: https://evil.example",
        " https://claude.ai/api/mcp/auth_callback",
        "https://claude.ai/api/mcp/auth_callback ",
        "https://claude.ai/api/mcp/auth_callback\t"
    ];

    private static readonly string[] ChatGptTricks =
    [
        "https://chatgpt.com/connector/oauth/abc/def",
        "https://chatgpt.com/connector/oauth/abc/",
        "https://chatgpt.com/connector/oauth/",
        "https://chatgpt.com/connector/oauth",
        "https://chatgpt.com/connector/oauthevil",
        "https://chatgpt.com/connector/oauth/abc?next=https://evil.example",
        "https://chatgpt.com/connector/oauth/abc#x",
        "https://chatgpt.com/connector/oauth/a%2fb",
        "https://chatgpt.com/connector/oauth/%2e%2e",
        "https://chatgpt.com/connector/oauth/..",
        "https://chatgpt.com/connector/oauth/../evil",
        "https://chatgpt.com/connector/oauth/abc\n",
        "https://chatgpt.com/connector/oauth//abc",
        "https://chatgpt.com/connector/oauth/a.b",
        "https://chatgpt.com/connector_platform_oauth_redirect/",
        "https://chatgpt.com/connector_platform_oauth_redirect?x=1",
        "https://chatgpt.com:8443/connector/oauth/abc"
    ];

    private static readonly string[] LoopbackTricks =
    [
        "http://127.0.0.2:8080/callback",
        "http://127.1:8080/callback",
        "http://0.0.0.0:8080/callback",
        "http://0x7f.0.0.1:8080/callback",
        "http://2130706433:8080/callback",
        "http://localhost.evil.example/callback",
        "http://localhost.:8080/callback",
        "http://LOCALHOST:8080/callback",
        "http://localhost@evil.example/callback",
        "http://localhost:8080@evil.example/callback",
        "http://127.0.0.1@evil.example:8080/callback",
        "http://evil.example/http://localhost:8080/callback",
        "http://localhost:99999/callback",
        "http://localhost:65536/callback",
        "http://localhost:0/callback",
        "http://localhost:abc/callback",
        "http://localhost:/callback",
        "https://localhost:8080/callback",
        "https://127.0.0.1:8080/callback",
        "http://[::2]:8080/callback",
        "http://[::1/callback",
        "http://[::ffff:127.0.0.1]:8080/callback",
        "http://localhost:8080/callback#fragment",
        "http://localhost:8080/callback?code=1#fragment",
        "http://localhost:8080/call back",
        "http://localhost:8080/call\\back",
        "http://localhost:8080\\@evil.example/callback",
        "http://localhost:8080/%2e%2e/callback",
        "http://localhost:8080/callback\n",
        "http://localhost:8080/callback?x=a b",
        "http://localhost:8080/callback?x=\\evil"
    ];

    private static readonly string[] DocumentedForeignAddresses =
    [
        "https://evil.example/callback",
        "https://example.com",
        "https://localhost.evil.example/callback",
        "https://vscode.dev/redirect/extra",
        "https://vscode.dev/redirect?x=1",
        "https://insiders.vscode.dev/redirect",
        "https://vscode.dev.evil.example/redirect",
        "http://vscode.dev/redirect",
        "https://VSCODE.DEV/redirect",
        "",
        "   ",
        "null"
    ];

    [TestCaseSource(nameof(ForeignHostTricks))]
    [TestCaseSource(nameof(SchemePortAndCaseTricks))]
    [TestCaseSource(nameof(IdnAndEscapeTricks))]
    [TestCaseSource(nameof(ChatGptTricks))]
    [TestCaseSource(nameof(LoopbackTricks))]
    [TestCaseSource(nameof(DocumentedForeignAddresses))]
    public void Match_HostileOrMalformedRedirect_IsNotTrusted(string redirectUri)
    {
        OAuthRedirectMatcher.Match(redirectUri, allowLoopback: true, NoCustomTargets).ShouldBeNull();
    }

    [Test]
    public void Match_NullOrOverlongRedirect_IsNotTrusted()
    {
        OAuthRedirectMatcher.Match(null, allowLoopback: true, NoCustomTargets).ShouldBeNull();
        OAuthRedirectMatcher.Match(ChatGptPrefix + new string('a', OAuthTrustedRedirects.MaxRedirectUriLength), true, NoCustomTargets).ShouldBeNull();
    }

    [TestCase("https://claude.ai/api/mcp/auth_callback", "Claude", "claude.ai")]
    [TestCase("https://claude.com/api/mcp/auth_callback", "Claude", "claude.com")]
    [TestCase("https://chatgpt.com/connector_platform_oauth_redirect", "ChatGPT", "chatgpt.com")]
    [TestCase("https://chatgpt.com/connector/oauth/abc", "ChatGPT", "chatgpt.com")]
    [TestCase("https://chatgpt.com/connector/oauth/Ab_9-Z", "ChatGPT", "chatgpt.com")]
    public void Match_ListedVendorRedirect_IsTrustedAsVendor(string redirectUri, string displayName, string host)
    {
        var match = OAuthRedirectMatcher.Match(redirectUri, allowLoopback: false, NoCustomTargets);

        match.ShouldNotBeNull();
        match!.DisplayName.ShouldBe(displayName);
        match.Host.ShouldBe(host);
        match.Class.ShouldBe(OAuthRedirectClass.Vendor);
        match.Port.ShouldBeNull();
    }

    [Test]
    public void Match_ChatGptCallbackIdAtLimit_IsTrusted_OneOverIsNot()
    {
        var atLimit = ChatGptPrefix + new string('a', OAuthTrustedRedirects.ChatGptCallbackIdMaxLength);
        var overLimit = atLimit + "a";

        OAuthRedirectMatcher.Match(atLimit, false, NoCustomTargets).ShouldNotBeNull();
        OAuthRedirectMatcher.Match(overLimit, false, NoCustomTargets).ShouldBeNull();
    }

    [TestCase("http://localhost:33418/callback", "localhost", 33418)]
    [TestCase("http://127.0.0.1:8080/", "127.0.0.1", 8080)]
    [TestCase("http://127.0.0.1:65535/callback/abc-123", "127.0.0.1", 65535)]
    [TestCase("http://[::1]:5000/cb", "[::1]", 5000)]
    [TestCase("http://localhost:1/callback?state=a&x=b", "localhost", 1)]
    [TestCase("http://localhost/callback", "localhost", null)]
    [TestCase("http://localhost", "localhost", null)]
    [TestCase("http://localhost:8787/oauth/callback", "localhost", 8787)]
    public void Match_LoopbackRedirect_IsTrustedAsLoopbackWithPort(string redirectUri, string host, int? port)
    {
        var match = OAuthRedirectMatcher.Match(redirectUri, allowLoopback: true, NoCustomTargets);

        match.ShouldNotBeNull();
        match!.Class.ShouldBe(OAuthRedirectClass.Loopback);
        match.DisplayName.ShouldBe(OAuthTrustedRedirects.LocalProgramDisplayName);
        match.Host.ShouldBe(host);
        match.Port.ShouldBe(port);
    }

    [Test]
    public void Match_VsCodeWebRedirect_IsLoopbackClassOnly()
    {
        var withLoopback = OAuthRedirectMatcher.Match(OAuthTrustedRedirects.VsCodeWebRedirectUri, true, NoCustomTargets);
        var withoutLoopback = OAuthRedirectMatcher.Match(OAuthTrustedRedirects.VsCodeWebRedirectUri, false, NoCustomTargets);

        withLoopback.ShouldNotBeNull();
        withLoopback!.Class.ShouldBe(OAuthRedirectClass.Loopback);
        withLoopback.DisplayName.ShouldBe(OAuthTrustedRedirects.LocalProgramDisplayName);
        withLoopback.Host.ShouldBe(OAuthTrustedRedirects.VsCodeWebHost);
        withoutLoopback.ShouldBeNull();
    }

    [TestCase("http://localhost:33418/callback")]
    [TestCase("http://127.0.0.1:8080/")]
    [TestCase("http://[::1]:5000/cb")]
    public void Match_LoopbackRedirectWithLoopbackSwitchOff_IsNotTrusted(string redirectUri)
    {
        OAuthRedirectMatcher.Match(redirectUri, allowLoopback: false, NoCustomTargets).ShouldBeNull();
    }

    [Test]
    public void Match_LoopbackSwitchOff_DoesNotAffectVendorOrCustomTargets()
    {
        OAuthRedirectMatcher.Match(ClaudeCallback, false, NoCustomTargets).ShouldNotBeNull();
        OAuthRedirectMatcher.Match(CustomTarget, false, [CustomTarget]).ShouldNotBeNull();
    }

    [Test]
    public void Match_CustomTarget_IsTrustedAsCustomWithHostAsName()
    {
        var match = OAuthRedirectMatcher.Match(CustomTarget, allowLoopback: true, [CustomTarget]);

        match.ShouldNotBeNull();
        match!.Class.ShouldBe(OAuthRedirectClass.Custom);
        match.Host.ShouldBe("mcp.example.org");
        match.DisplayName.ShouldBe("mcp.example.org");
    }

    [TestCase("https://mcp.example.org/oauth/callback/")]
    [TestCase("https://mcp.example.org/oauth/callback?x=1")]
    [TestCase("https://MCP.example.org/oauth/callback")]
    [TestCase("https://mcp.example.org:443/oauth/callback")]
    [TestCase("https://mcp.example.org/oauth/callback/extra")]
    [TestCase("https://mcp.example.org/oauth/../oauth/callback")]
    [TestCase("https://sub.mcp.example.org/oauth/callback")]
    [TestCase("https://example.org/oauth/callback")]
    [TestCase("http://mcp.example.org/oauth/callback")]
    public void Match_NearMissOfCustomTarget_IsNotTrusted(string redirectUri)
    {
        OAuthRedirectMatcher.Match(redirectUri, allowLoopback: true, [CustomTarget]).ShouldBeNull();
    }

    [Test]
    public void Match_CustomTarget_DoesNotMakeItsHostAPrefix()
    {
        OAuthRedirectMatcher.Match("https://mcp.example.org/other", true, [CustomTarget]).ShouldBeNull();
    }

    [TestCase("https://evil.example/callback", "https://evil.example")]
    [TestCase("https://evil.example:8443/a/b?x=1", "https://evil.example:8443")]
    [TestCase("cursor://anysphere.cursor-retrieval/oauth", "cursor://anysphere.cursor-retrieval")]
    [TestCase("https://evil.example", "https://evil.example")]
    [TestCase("not a url", "(invalid address)")]
    [TestCase("", "(invalid address)")]
    public void DescribeTarget_NamesSchemeAndAuthority(string redirectUri, string expected)
    {
        OAuthRedirectMatcher.DescribeTarget(redirectUri).ShouldBe(expected);
    }

    [Test]
    public void DescribeTarget_ControlCharacters_AreNotEchoed()
    {
        OAuthRedirectMatcher.DescribeTarget("https://evil.example\r\nX-Injected: 1/").ShouldBe("(invalid address)");
    }

    [Test]
    public void DescribeTarget_LongAuthority_IsTruncated()
    {
        var described = OAuthRedirectMatcher.DescribeTarget("https://" + new string('a', 500) + "/x");

        described.Length.ShouldBeLessThanOrEqualTo(100);
    }
}
