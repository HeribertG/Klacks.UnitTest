// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.OAuth;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthCustomTargetParserTests
{
    [TestCase("https://mcp.example.org/oauth/callback", "https://mcp.example.org/oauth/callback")]
    [TestCase("  https://mcp.example.org/oauth/callback  ", "https://mcp.example.org/oauth/callback")]
    [TestCase("HTTPS://MCP.Example.ORG/oauth/Callback", "https://mcp.example.org/oauth/Callback")]
    [TestCase("https://mcp.example.org:443/cb", "https://mcp.example.org/cb")]
    [TestCase("https://mcp.example.org", "https://mcp.example.org")]
    [TestCase("https://mcp.example.org/", "https://mcp.example.org/")]
    [TestCase("https://gateway/cb", "https://gateway/cb")]
    [TestCase("https://bücher.example/cb", "https://xn--bcher-kva.example/cb")]
    [TestCase("https://xn--bcher-kva.example/cb", "https://xn--bcher-kva.example/cb")]
    [TestCase("https://a-b.example.org/a_b/c-d/e.f/g~h", "https://a-b.example.org/a_b/c-d/e.f/g~h")]
    public void TryCanonicalize_ValidTarget_ReturnsCanonicalForm(string input, string expected)
    {
        OAuthCustomTargetParser.TryCanonicalize(input, out var canonical, out var error).ShouldBeTrue(error);

        canonical.ShouldBe(expected);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("mcp.example.org/cb")]
    [TestCase("http://mcp.example.org/cb")]
    [TestCase("ftp://mcp.example.org/cb")]
    [TestCase("https://")]
    [TestCase("https:///cb")]
    [TestCase("https://user@mcp.example.org/cb")]
    [TestCase("https://user:pw@mcp.example.org/cb")]
    [TestCase("https://mcp.example.org:8443/cb")]
    [TestCase("https://mcp.example.org:80/cb")]
    [TestCase("https://mcp.example.org:/cb")]
    [TestCase("https://mcp.example.org/cb?x=1")]
    [TestCase("https://mcp.example.org/cb#frag")]
    [TestCase("https://mcp.example.org/c%62")]
    [TestCase("https://mcp.example.org/%2e%2e/cb")]
    [TestCase("https://mcp.example.org/a/../cb")]
    [TestCase("https://mcp.example.org/./cb")]
    [TestCase("https://mcp.example.org/..")]
    [TestCase("https://mcp.example.org//cb")]
    [TestCase("https://mcp.example.org/a//b")]
    [TestCase("https://mcp.example.org\\cb")]
    [TestCase("https://mcp.example.org/a\\b")]
    [TestCase("https://mcp.example.org/a b")]
    [TestCase("https://mcp .example.org/cb")]
    [TestCase("https://*.example.org/cb")]
    [TestCase("https://mcp.example.org/*")]
    [TestCase("https://mcp.example.org/cb/(a|b)")]
    [TestCase("https://mcp.example.org./cb")]
    [TestCase("https://.example.org/cb")]
    [TestCase("https://mcp..example.org/cb")]
    [TestCase("https://-mcp.example.org/cb")]
    [TestCase("https://localhost/cb")]
    [TestCase("https://LOCALHOST/cb")]
    [TestCase("https://127.0.0.1/cb")]
    [TestCase("https://10.1.2.3/cb")]
    [TestCase("https://[::1]/cb")]
    [TestCase("https://2130706433/cb")]
    public void TryCanonicalize_InvalidTarget_IsRefusedWithReason(string input)
    {
        OAuthCustomTargetParser.TryCanonicalize(input, out var canonical, out var error).ShouldBeFalse();

        canonical.ShouldBeEmpty();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void TryCanonicalize_TooLongTarget_IsRefused()
    {
        var input = "https://mcp.example.org/" + new string('a', OAuthTrustedRedirects.MaxRedirectUriLength);

        OAuthCustomTargetParser.TryCanonicalize(input, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void TryCanonicalize_HostLabelLongerThan63_IsRefused()
    {
        OAuthCustomTargetParser.TryCanonicalize("https://" + new string('a', 64) + ".example/cb", out _, out _).ShouldBeFalse();
    }

    [Test]
    public void ParseCanonical_ReturnsDistinctCanonicalTargetsAndSkipsInvalidOnes()
    {
        var json = "[\"https://MCP.example.org/cb\",\"https://mcp.example.org/cb\",\"https://*.evil.example/cb\",\"http://x.example/cb\",\"https://other.example/cb\"]";

        var result = OAuthCustomTargetParser.ParseCanonical(json);

        result.ShouldBe(["https://mcp.example.org/cb", "https://other.example/cb"]);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("{\"a\":1}")]
    [TestCase("[1,2]")]
    [TestCase("\"https://mcp.example.org/cb\"")]
    public void ParseCanonical_EmptyOrUnparseableValue_YieldsNoTargets(string? json)
    {
        OAuthCustomTargetParser.ParseCanonical(json).ShouldBeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("[]")]
    [TestCase("[\"https://mcp.example.org/cb\"]")]
    [TestCase("[\"https://mcp.example.org/cb\",\"https://other.example/cb\"]")]
    public void TryValidate_AcceptableValue_IsAccepted(string? json)
    {
        OAuthCustomTargetParser.TryValidate(json, out var error).ShouldBeTrue(error);
    }

    [TestCase("not json")]
    [TestCase("{\"a\":1}")]
    [TestCase("[1]")]
    [TestCase("[\"https://mcp.example.org/cb\",\"https://*.evil.example/cb\"]")]
    [TestCase("[\"http://mcp.example.org/cb\"]")]
    [TestCase("[null]")]
    public void TryValidate_ValueWithInvalidEntryOrShape_IsRefused(string json)
    {
        OAuthCustomTargetParser.TryValidate(json, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void TryValidate_MoreThanMaxTargets_IsRefused()
    {
        var targets = Enumerable.Range(0, OAuthTrustedRedirects.MaxCustomTargets + 1).Select(i => $"\"https://h{i}.example/cb\"");
        var json = "[" + string.Join(",", targets) + "]";

        OAuthCustomTargetParser.TryValidate(json, out _).ShouldBeFalse();
    }
}
