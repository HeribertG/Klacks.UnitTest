// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for the map tile Content-Security-Policy of the reverse proxy. The img-src of both nginx templates (prod
/// deploy/nginx-proxy and on-prem deploy/onprem/nginx) comes from MAP_TILE_CSP_SOURCES, so an operator can allow an own
/// tile server or no tile host at all. That only works when every Compose file that mounts a template also hands the
/// variable to envsubst and defines it for the nginx container: a missing envsubst entry leaves the literal
/// ${MAP_TILE_CSP_SOURCES} in nginx.conf, which nginx rejects as an unknown variable.
///
/// Checked:
/// - both templates carry ${MAP_TILE_CSP_SOURCES} in the img-src of the Klacks app and no img-src names a fixed public
///   tile host (other server blocks such as the marketing site keep their own CSP without tiles);
/// - every Compose file of Klacks.Api that mounts an nginx template (docker-compose.yml,
///   deploy/docker-compose-server.yml, deploy/onprem/docker-compose.yml, found by scanning) lists
///   $$MAP_TILE_CSP_SOURCES in the envsubst variable list of that service and defines MAP_TILE_CSP_SOURCES in its
///   environment with ${VAR-default} (an empty value set on purpose must stay empty, so not ":-");
/// - the Compose defaults, the default in deploy/onprem/.env.example and the installer defaults are the same;
/// - the on-prem Compose file forwards GEO_NO_OUTBOUND_TRAFFIC to klacks-api as GeoServices__NoOutboundTraffic;
/// - both installers fill MAP_TILE_CSP_SOURCES only when the key is absent, never when it is merely empty.
///
/// Not covered: the Compose file of the super repository (C:\SourceCode\docker-compose-server.yml), which is not part
/// of this repository, and whether the rendered configuration passes nginx -t.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class MapTileCspGuardTests
{
    private const string CspVariablePlaceholder = "${MAP_TILE_CSP_SOURCES}";
    private const string EnvsubstVariable = "$$MAP_TILE_CSP_SOURCES";
    private const string EnvironmentWithDefaultPrefix = "MAP_TILE_CSP_SOURCES=${MAP_TILE_CSP_SOURCES-";
    private const string EnvironmentWithColonDefault = "${MAP_TILE_CSP_SOURCES:-";
    private const string NoOutboundForwarding = "GeoServices__NoOutboundTraffic=${GEO_NO_OUTBOUND_TRAFFIC";
    private const string ApiServiceName = "klacks-api";
    private const string ComposeFilePattern = "*.yml";
    private const string TemplateMountMarker = ".conf.template:/etc/nginx/";
    private const int ExpectedTemplateMountingComposeFiles = 3;

    private static readonly string[] SkippedDirectoryNames = ["bin", "obj", "node_modules", ".git"];

    private static readonly string[][] TemplateSegments =
    [
        ["deploy", "nginx-proxy", "nginx.conf.template"],
        ["deploy", "onprem", "nginx", "nginx.onprem.conf.template"]
    ];

    private static readonly string[] OnPremComposeSegments = ["deploy", "onprem", "docker-compose.yml"];
    private static readonly string[] OnPremEnvExampleSegments = ["deploy", "onprem", ".env.example"];
    private static readonly string[] InstallShSegments = ["deploy", "onprem", "install.sh"];
    private static readonly string[] InstallPs1Segments = ["deploy", "onprem", "install.ps1"];

    private static readonly string[] PublicTileHosts =
    [
        "tile.openstreetmap.org",
        "tile.openstreetmap.de",
        "basemaps.cartocdn.com",
        "opentopomap.org"
    ];

    private static readonly Regex ImgSrcDirective = new(@"img-src\s+(?<sources>[^;]*);", RegexOptions.Compiled);
    private static readonly Regex ServiceStart = new(@"^  (?<name>[A-Za-z0-9_.-]+):\s*$", RegexOptions.Compiled);
    private static readonly Regex TopLevelKey = new(@"^\S", RegexOptions.Compiled);
    private static readonly Regex EnvsubstList = new(@"envsubst\s+'(?<variables>[^']*)'", RegexOptions.Compiled);
    private static readonly Regex ComposeDefault = new(@"MAP_TILE_CSP_SOURCES=\$\{MAP_TILE_CSP_SOURCES-(?<value>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex EnvExampleValue = new(@"(?m)^MAP_TILE_CSP_SOURCES=(?<value>[^\r\n]*)\r?$", RegexOptions.Compiled);
    private static readonly Regex InstallShDefault = new(@"(?m)^set_if_absent MAP_TILE_CSP_SOURCES ""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex InstallPs1Default = new(@"(?m)^Set-IfAbsent 'MAP_TILE_CSP_SOURCES' '(?<value>[^']*)'", RegexOptions.Compiled);

    [Test]
    public void BothTemplates_TakeTheTileSourcesFromTheVariable_AndNameNoFixedTileHost()
    {
        foreach (var segments in TemplateSegments)
        {
            var path = ApiFile(segments);
            var directives = ImgSrcDirective.Matches(File.ReadAllText(path)).ToList();

            var sources = directives.Select(directive => directive.Groups["sources"].Value).ToList();
            sources.ShouldContain(
                source => source.Contains(CspVariablePlaceholder, StringComparison.Ordinal),
                $"{path}: the img-src of the Klacks app must take the tile hosts from {CspVariablePlaceholder}.");
            sources.SelectMany(source => PublicTileHosts.Where(host => source.Contains(host, StringComparison.OrdinalIgnoreCase)))
                .ShouldBeEmpty($"{path}: an img-src names a fixed tile host; it belongs into MAP_TILE_CSP_SOURCES.");
        }
    }

    [Test]
    public void EveryComposeFileMountingATemplate_SubstitutesAndDefinesTheVariable()
    {
        var mountingFiles = ComposeFiles()
            .Where(path => File.ReadAllText(path).Contains(TemplateMountMarker, StringComparison.Ordinal))
            .ToList();

        mountingFiles.Count.ShouldBe(ExpectedTemplateMountingComposeFiles,
            "Expected docker-compose.yml, deploy/docker-compose-server.yml and deploy/onprem/docker-compose.yml: " +
            string.Join(", ", mountingFiles));

        var problems = new List<string>();
        foreach (var path in mountingFiles)
        {
            foreach (var (name, block) in ServiceBlocks(path).Where(service => service.Block.Contains(TemplateMountMarker, StringComparison.Ordinal)))
            {
                var envsubst = EnvsubstList.Match(block);
                if (!envsubst.Success || !envsubst.Groups["variables"].Value.Split(' ').Contains(EnvsubstVariable, StringComparer.Ordinal))
                {
                    problems.Add($"{path} ({name}): envsubst does not substitute {EnvsubstVariable}");
                }

                if (!block.Contains(EnvironmentWithDefaultPrefix, StringComparison.Ordinal))
                {
                    problems.Add($"{path} ({name}): environment does not define {EnvironmentWithDefaultPrefix}...}}");
                }

                if (block.Contains(EnvironmentWithColonDefault, StringComparison.Ordinal))
                {
                    problems.Add($"{path} ({name}): ':-' would replace a deliberately empty MAP_TILE_CSP_SOURCES by the default");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void ComposeEnvExampleAndInstallerDefaults_AreTheSame()
    {
        var composeDefaults = ComposeFiles()
            .SelectMany(path => ComposeDefault.Matches(File.ReadAllText(path)).Select(match => match.Groups["value"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        composeDefaults.ShouldHaveSingleItem(string.Join(" | ", composeDefaults));
        var expected = composeDefaults[0];

        expected.ShouldNotBeNullOrWhiteSpace();
        SingleValue(EnvExampleValue, OnPremEnvExampleSegments).ShouldBe(expected);
        SingleValue(InstallShDefault, InstallShSegments).ShouldBe(expected);
        SingleValue(InstallPs1Default, InstallPs1Segments).ShouldBe(expected);
    }

    [Test]
    public void OnPremCompose_ForwardsTheNoOutboundSwitchToTheApi()
    {
        var path = ApiFile(OnPremComposeSegments);
        var apiBlock = ServiceBlocks(path).Single(service => service.Name == ApiServiceName).Block;

        apiBlock.ShouldContain(NoOutboundForwarding, Case.Sensitive);
    }

    [Test]
    public void Installers_FillTheCspSourcesOnlyWhenTheKeyIsAbsent()
    {
        var installSh = File.ReadAllText(ApiFile(InstallShSegments));
        var installPs1 = File.ReadAllText(ApiFile(InstallPs1Segments));

        installSh.ShouldContain("set_if_absent() { local k=\"$1\" v=\"$2\"; [ -z \"${ENVMAP[$k]+x}\" ]", Case.Sensitive);
        installSh.ShouldNotContain("set_if_empty MAP_TILE_CSP_SOURCES", Case.Sensitive);
        installPs1.ShouldContain("function Set-IfAbsent($key, $value) { if (-not $envMap.Contains($key))", Case.Sensitive);
        installPs1.ShouldNotContain("Set-IfEmpty 'MAP_TILE_CSP_SOURCES'", Case.Sensitive);
    }

    private static string SingleValue(Regex pattern, string[] segments)
    {
        var path = ApiFile(segments);
        var matches = pattern.Matches(File.ReadAllText(path));
        matches.Count.ShouldBe(1, $"{path} must set MAP_TILE_CSP_SOURCES exactly once.");
        return matches[0].Groups["value"].Value.Trim();
    }

    private static string ApiFile(string[] segments)
    {
        var path = Path.Combine(RepositoryRootLocator.ApiProject, Path.Combine(segments));
        File.Exists(path).ShouldBeTrue($"{path} does not exist.");
        return path;
    }

    private static List<string> ComposeFiles()
    {
        return Directory.EnumerateFiles(RepositoryRootLocator.ApiProject, ComposeFilePattern, SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(RepositoryRootLocator.ApiProject, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => SkippedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    private static List<(string Name, string Block)> ServiceBlocks(string path)
    {
        var blocks = new List<(string Name, string Block)>();
        string? currentName = null;
        var currentLines = new List<string>();

        foreach (var line in File.ReadAllLines(path))
        {
            var start = ServiceStart.Match(line);
            if (start.Success || TopLevelKey.IsMatch(line))
            {
                if (currentName != null)
                {
                    blocks.Add((currentName, string.Join('\n', currentLines)));
                }

                currentName = start.Success ? start.Groups["name"].Value : null;
                currentLines.Clear();
                continue;
            }

            if (currentName != null)
            {
                currentLines.Add(line);
            }
        }

        if (currentName != null)
        {
            blocks.Add((currentName, string.Join('\n', currentLines)));
        }

        return blocks;
    }
}
