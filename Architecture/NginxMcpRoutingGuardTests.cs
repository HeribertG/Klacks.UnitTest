// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for the reverse-proxy templates in front of klacks-api: every nginx server block that routes
/// /api/ to klacks-api must also route the MCP endpoint and the OAuth discovery/authorization paths
/// there. Without those locations the SPA fallback of klacks-ui answers /mcp,
/// /.well-known/oauth-* and /oauth/* with index.html, so MCP clients cannot connect from outside
/// (found 2026-10-03 for the on-prem template and both public prod blocks).
///
/// Checked per API-serving server block (on-prem 443, prod :7643 and play.klacks-software.ch):
/// - exact location for the MCP route with streaming settings (no proxy buffering, no proxy cache,
///   no gzip, HTTP/1.1 upstream, long read/send timeouts) and no own add_header/access_log;
/// - prefix locations for the RFC 9728/8414 well-known documents and for the /oauth/ endpoints.
/// Per template: exactly one access_log directive (the redacted one at http level), so no new
/// location can bypass the query-string redaction.
/// Per compose file: klacks-api sets Mcp__PublicBaseUrl to a public https origin, because the
/// appsettings default (https://localhost:5001) ends up in the OAuth metadata otherwise.
///
/// Not covered: whether the configuration is valid nginx syntax (checked by hand with nginx -t) and
/// blocks that proxy everything to klacks-api with location / (prod :5443), which need no extra rule.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class NginxMcpRoutingGuardTests
{
    private const string ApiUpstreamProxyPass = "proxy_pass http://klacks-api";
    private const string ApiPrefixLocation = "/api/";
    private const string OAuthWellKnownNamePrefix = "oauth-";
    private const string ExactMatchModifier = "=";
    private const string HttpsScheme = "https://";
    private const int MinimumStreamTimeoutSeconds = 3600;

    private static readonly string[] OnPremTemplateSegments = ["deploy", "onprem", "nginx", "nginx.onprem.conf.template"];
    private static readonly string[] ProdTemplateSegments = ["deploy", "nginx-proxy", "nginx.conf.template"];
    private static readonly string[] OnPremComposeSegments = ["deploy", "onprem", "docker-compose.yml"];
    private static readonly string[] ProdComposeSegments = ["deploy", "docker-compose-server.yml"];

    private static readonly string[] RequiredStreamingDirectives =
    [
        ApiUpstreamProxyPass + ";",
        "proxy_http_version 1.1;",
        "proxy_buffering off;",
        "proxy_cache off;",
        "gzip off;"
    ];

    private static readonly string[] ForbiddenLocationDirectives = ["add_header", "access_log"];

    private static readonly Regex CommentPattern = new(@"#[^\n]*", RegexOptions.Compiled);
    private static readonly Regex ServerBlockStart = new(@"(?m)^\s*server\s*\{", RegexOptions.Compiled);
    private static readonly Regex LocationBlockStart = new(@"location\s+(?:(?<modifier>=|\^~)\s+)?(?<path>[^\s{]+)\s*\{", RegexOptions.Compiled);
    private static readonly Regex AccessLogDirective = new(@"(?m)^\s*access_log\s", RegexOptions.Compiled);
    private static readonly Regex TimeoutDirective = new(@"(?<name>proxy_read_timeout|proxy_send_timeout)\s+(?<seconds>\d+)s;", RegexOptions.Compiled);
    private static readonly Regex PublicBaseUrlEntry = new(@"(?m)^\s*-\s*Mcp__PublicBaseUrl=(?<value>\S+)\s*$", RegexOptions.Compiled);

    private static IEnumerable<TestCaseData> Templates()
    {
        yield return new TestCaseData((object)OnPremTemplateSegments).SetArgDisplayNames("onprem");
        yield return new TestCaseData((object)ProdTemplateSegments).SetArgDisplayNames("prod");
    }

    private static IEnumerable<TestCaseData> ComposeFiles()
    {
        yield return new TestCaseData((object)OnPremComposeSegments).SetArgDisplayNames("onprem");
        yield return new TestCaseData((object)ProdComposeSegments).SetArgDisplayNames("prod");
    }

    [TestCaseSource(nameof(Templates))]
    public void EveryApiServerBlock_MustRouteMcpAndOAuthToKlacksApi(string[] templateSegments)
    {
        var apiServerBlocks = ReadServerBlocks(templateSegments)
            .Where(block => block.Any(location => location.Path == ApiPrefixLocation && location.Body.Contains(ApiUpstreamProxyPass)))
            .ToList();

        apiServerBlocks.ShouldNotBeEmpty("the template has no server block that routes /api/ to klacks-api — the guard would check nothing.");

        foreach (var locations in apiServerBlocks)
        {
            var mcp = locations.SingleOrDefault(location =>
                location.Modifier == ExactMatchModifier && location.Path == McpServerConstants.RoutePattern);
            mcp.ShouldNotBeNull($"a server block routes /api/ to klacks-api but has no 'location = {McpServerConstants.RoutePattern}'.");
            AssertStreamingLocation(mcp);

            AssertPrefixRoutedToApi(locations, $"/{OAuthConstants.WellKnownSegment}/{OAuthWellKnownNamePrefix}");
            AssertPrefixRoutedToApi(locations, $"/{OAuthConstants.RouteBase}/");
        }
    }

    [TestCaseSource(nameof(Templates))]
    public void Template_MustDeclareTheRedactedAccessLogOnlyOnce(string[] templateSegments)
    {
        var content = StripComments(File.ReadAllText(ResolveApiPath(templateSegments)));

        AccessLogDirective.Matches(content).Count.ShouldBe(1,
            "only the redacted access_log at http level may exist; a server or location block with its own access_log bypasses the token redaction.");
    }

    [TestCaseSource(nameof(ComposeFiles))]
    public void KlacksApi_MustReceiveAPublicMcpBaseUrl(string[] composeSegments)
    {
        var content = File.ReadAllText(ResolveApiPath(composeSegments));
        var match = PublicBaseUrlEntry.Match(content);

        match.Success.ShouldBeTrue("klacks-api has no Mcp__PublicBaseUrl — the OAuth metadata would advertise the appsettings default.");
        var value = match.Groups["value"].Value;
        value.ShouldContain(HttpsScheme);
        value.ShouldNotContain(McpPublicEndpointOptions.DefaultPublicBaseUrl);
    }

    private static void AssertStreamingLocation(NginxLocation location)
    {
        foreach (var directive in RequiredStreamingDirectives)
        {
            location.Body.ShouldContain(directive, Case.Sensitive,
                $"location = {location.Path} must contain '{directive}' so streamed MCP answers are neither buffered nor compressed.");
        }

        var timeouts = TimeoutDirective.Matches(location.Body)
            .ToDictionary(match => match.Groups["name"].Value, match => int.Parse(match.Groups["seconds"].Value));
        timeouts.Count.ShouldBe(2, $"location = {location.Path} must set proxy_read_timeout and proxy_send_timeout explicitly.");
        timeouts.Values.ShouldAllBe(seconds => seconds >= MinimumStreamTimeoutSeconds);

        AssertNoForbiddenDirectives(location);
    }

    private static void AssertPrefixRoutedToApi(IReadOnlyList<NginxLocation> locations, string prefix)
    {
        var location = locations.SingleOrDefault(candidate => candidate.Modifier is null && candidate.Path == prefix);
        location.ShouldNotBeNull($"a server block routes /api/ to klacks-api but has no 'location {prefix}'.");
        location.Body.ShouldContain(ApiUpstreamProxyPass, Case.Sensitive, $"location {prefix} must proxy to klacks-api.");
        AssertNoForbiddenDirectives(location);
    }

    private static void AssertNoForbiddenDirectives(NginxLocation location)
    {
        foreach (var directive in ForbiddenLocationDirectives)
        {
            location.Body.ShouldNotContain(directive, Case.Sensitive,
                $"location {location.Path} must not declare {directive}: it would drop the inherited security headers or bypass the log redaction.");
        }
    }

    private static List<List<NginxLocation>> ReadServerBlocks(string[] templateSegments)
    {
        var content = StripComments(File.ReadAllText(ResolveApiPath(templateSegments)));

        return ServerBlockStart.Matches(content)
            .Select(match => ReadBlockBody(content, match.Index + match.Length))
            .Select(ReadLocations)
            .ToList();
    }

    private static List<NginxLocation> ReadLocations(string serverBody)
    {
        return LocationBlockStart.Matches(serverBody)
            .Select(match => new NginxLocation(
                match.Groups["modifier"].Success ? match.Groups["modifier"].Value : null,
                match.Groups["path"].Value,
                ReadBlockBody(serverBody, match.Index + match.Length)))
            .ToList();
    }

    private static string ReadBlockBody(string content, int bodyStart)
    {
        var depth = 1;
        for (var index = bodyStart; index < content.Length; index++)
        {
            depth += content[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };

            if (depth == 0)
            {
                return content[bodyStart..index];
            }
        }

        throw new InvalidOperationException("Unbalanced braces in nginx template.");
    }

    private static string StripComments(string content)
    {
        return CommentPattern.Replace(content.ReplaceLineEndings("\n"), string.Empty);
    }

    private static string ResolveApiPath(string[] segments)
    {
        var path = Path.Combine(LocateApiProject(), Path.Combine(segments));
        File.Exists(path).ShouldBeTrue($"Expected deployment file not found: {path}");
        return path;
    }

    private static string LocateApiProject()
    {
        return RepositoryRootLocator.ApiProject;
    }

    private sealed record NginxLocation(string? Modifier, string Path, string Body);
}
