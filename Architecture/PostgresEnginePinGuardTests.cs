// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the pinned PostgreSQL engine. The single source is Klacks.Api/deploy/postgres/engine.json (major, version,
/// pgvector, image, windowsBinariesUrl, releaseSuffix). The Docker image recipe (deploy/postgres/Dockerfile), the native
/// Windows workflow, the CI service container and the on-prem compose files (and the marketplace mirror of the on-prem
/// bundle) must carry the same engine, so a Docker customer, a native customer and the engine artifact the updater installs
/// never differ. The server compose file is not pinned here: it stays on its old engine until the upgrade is done. The
/// engine artifact workflow and the manifest step of deploy.yml must read the pin instead of repeating it.
///
/// Klacks.Marketplace is a sibling repository; where it is not checked out its mirror is skipped, the same split
/// SttImagePinGuardTests makes.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PostgresEnginePinGuardTests
{
    private const string UnixLineEnding = "\n";
    private const string ImageRepositoryPrefix = "ghcr.io/heribertg/klacks-postgres:";
    private const string ImageLineMarker = "klacks-postgres:";
    private const string ImageLinePrefix = "image:";
    private const string EdbBinariesUrlPattern = @"^https://get\.enterprisedb\.com/postgresql/postgresql-(\d+\.\d+)-\d+-windows-x64-binaries\.zip$";
    private const string PgvectorPattern = @"^[0-9]+\.[0-9]+\.[0-9]+\z";
    private const string VersionPattern = @"^[0-9]+\.[0-9]+\z";
    private const string ReleaseSuffixPattern = @"^(-r[0-9]+)?\z";
    private const string Sha256Pattern = @"^[0-9a-f]{64}\z";
    private const string PinFileReference = "deploy/postgres/engine.json";

    private static readonly string[] PinSegments = ["Klacks.Api", "deploy", "postgres", "engine.json"];
    private static readonly string[] DockerfileSegments = ["Klacks.Api", "deploy", "postgres", "Dockerfile"];
    private static readonly string[] NativeWorkflowSegments = ["Klacks.Api", ".github", "workflows", "native-windows.yml"];
    private static readonly string[] TestsWorkflowSegments = ["Klacks.Api", ".github", "workflows", "tests.yml"];
    private static readonly string[] ArtifactWorkflowSegments = ["Klacks.Api", ".github", "workflows", "postgres-engine-artifact.yml"];
    private static readonly string[] DeployWorkflowSegments = ["Klacks.Api", ".github", "workflows", "deploy.yml"];
    private static readonly string[] OnPremComposeSegments = ["Klacks.Api", "deploy", "onprem", "docker-compose.yml"];
    private static readonly string[] MarketplaceComposeSegments =
        ["Klacks.Marketplace", "Resources", "OnPremTemplate", "docker-compose.yml"];

    [Test]
    public void PinFile_HoldsAConsistentEngineDescription()
    {
        var pin = ReadPin();

        pin.Major.ShouldBeGreaterThan(0);
        pin.Version.ShouldMatch(VersionPattern);
        pin.Version.Split('.')[0].ShouldBe(pin.Major.ToString());
        pin.Pgvector.ShouldMatch(PgvectorPattern);
        pin.Image.ShouldBe($"{ImageRepositoryPrefix}{pin.Major}");
        pin.WindowsBinariesSha256.ShouldMatch(Sha256Pattern);
        pin.ReleaseSuffix.ShouldMatch(ReleaseSuffixPattern);

        var urlMatch = Regex.Match(pin.WindowsBinariesUrl, EdbBinariesUrlPattern);
        urlMatch.Success.ShouldBeTrue($"windowsBinariesUrl '{pin.WindowsBinariesUrl}' is not an EDB binaries ZIP.");
        urlMatch.Groups[1].Value.ShouldBe(pin.Version, "windowsBinariesUrl must name the pinned PostgreSQL version.");
    }

    [Test]
    public void Dockerfile_BuildsThePinnedMajorVersionAndPgvector()
    {
        var pin = ReadPin();
        var dockerfile = Read(RepositoryRootLocator.RequireFile(DockerfileSegments));

        ReadArgDefault(dockerfile, "PG_MAJOR").ShouldBe(pin.Major.ToString());
        ReadArgDefault(dockerfile, "PG_VERSION").ShouldStartWith($"{pin.Version}-");
        ReadArgDefault(dockerfile, "PGVECTOR_VERSION").ShouldBe($"v{pin.Pgvector}");
    }

    [Test]
    public void NativeWindowsWorkflow_UsesThePinnedBinariesAndPgvector()
    {
        var pin = ReadPin();
        var workflow = Read(RepositoryRootLocator.RequireFile(NativeWorkflowSegments));

        workflow.ShouldContain($"POSTGRES_BINARIES_URL: '{pin.WindowsBinariesUrl}'");
        workflow.ShouldContain($"PGVECTOR_TAG: 'v{pin.Pgvector}'");
    }

    [Test]
    public void ComposeFilesAndTestServiceContainer_UseThePinnedImage()
    {
        var pin = ReadPin();

        foreach (var (name, content) in ReadImageConsumers())
        {
            var imageLines = content.Split(UnixLineEnding)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith(ImageLinePrefix, StringComparison.Ordinal) && line.Contains(ImageLineMarker, StringComparison.Ordinal))
                .ToList();

            imageLines.ShouldNotBeEmpty($"{name} has no klacks-postgres image line.");
            foreach (var line in imageLines)
            {
                line.ShouldContain(pin.Image, customMessage: $"{name} must use the pinned engine image ({pin.Image}): {line}");
            }
        }
    }

    [Test]
    public void EngineArtifactWorkflowAndManifestStep_ReadThePinFile()
    {
        var artifactWorkflow = Read(RepositoryRootLocator.RequireFile(ArtifactWorkflowSegments));
        var deployWorkflow = Read(RepositoryRootLocator.RequireFile(DeployWorkflowSegments));

        artifactWorkflow.ShouldContain(PinFileReference);
        artifactWorkflow.ShouldContain("windowsBinariesSha256");
        artifactWorkflow.ShouldContain("postgres-engine-");
        deployWorkflow.ShouldContain($"--engine-json Klacks.Api/{PinFileReference}");
        deployWorkflow.ShouldContain("Merge-EngineManifestBlock.sh");
    }

    private static List<(string Name, string Content)> ReadImageConsumers()
    {
        var files = new List<(string, string)>
        {
            (string.Join('/', OnPremComposeSegments), Read(RepositoryRootLocator.RequireFile(OnPremComposeSegments))),
            (string.Join('/', TestsWorkflowSegments), Read(RepositoryRootLocator.RequireFile(TestsWorkflowSegments))),
        };

        var marketplace = RepositoryRootLocator.FindFile(MarketplaceComposeSegments);
        if (marketplace != null)
        {
            files.Add((string.Join('/', MarketplaceComposeSegments), Read(marketplace)));
        }

        return files;
    }

    private static string ReadArgDefault(string dockerfile, string name)
    {
        var match = Regex.Match(dockerfile, $@"^ARG {Regex.Escape(name)}=(.+)$", RegexOptions.Multiline);
        match.Success.ShouldBeTrue($"Dockerfile has no 'ARG {name}=<default>' line.");
        return match.Groups[1].Value.Trim();
    }

    private static EnginePin ReadPin()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(PinSegments)));
        var root = document.RootElement;

        return new EnginePin(
            root.GetProperty("major").GetInt32(),
            root.GetProperty("version").GetString() ?? string.Empty,
            root.GetProperty("pgvector").GetString() ?? string.Empty,
            root.GetProperty("image").GetString() ?? string.Empty,
            root.GetProperty("windowsBinariesUrl").GetString() ?? string.Empty,
            root.GetProperty("windowsBinariesSha256").GetString() ?? string.Empty,
            root.TryGetProperty("releaseSuffix", out var suffix) ? suffix.GetString() ?? string.Empty : string.Empty);
    }

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings(UnixLineEnding);

    private sealed record EnginePin(int Major, string Version, string Pgvector, string Image, string WindowsBinariesUrl, string WindowsBinariesSha256, string ReleaseSuffix);
}
