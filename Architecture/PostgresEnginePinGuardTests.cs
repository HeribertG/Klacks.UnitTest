// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the pinned PostgreSQL engine. The single source is Klacks.Api/deploy/postgres/engine.json (major, version,
/// pgvector, image, and per runtime identifier under "native": win-x64 with the EDB windowsBinariesUrl/Sha256, win-arm64
/// with the hashes of the self-built engine, each with its own releaseSuffix). The win-arm64 build pins
/// (deploy/postgres/win-arm64/pins.json) must name the same PostgreSQL version, source hash, pgvector version and commit, and
/// every RID must publish under its own release tag. The win-arm64 artifact workflow runs only by hand, so a merge never
/// starts the build or the code signing. The Docker image recipe (deploy/postgres/Dockerfile), the native
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
    private const string WinX64Rid = "win-x64";
    private const string WinArm64Rid = "win-arm64";
    private const string Arm64PinFileReference = "deploy/postgres/win-arm64/pins.json";
    private const string Arm64Runner = "runs-on: windows-11-arm";
    private const string CodeSigningEnvironment = "environment: certum-signing";
    private const string ManualTriggerOnly = "on:\n  workflow_dispatch:\n\nconcurrency:";

    private static readonly string[] PinSegments = ["Klacks.Api", "deploy", "postgres", "engine.json"];
    private static readonly string[] DockerfileSegments = ["Klacks.Api", "deploy", "postgres", "Dockerfile"];
    private static readonly string[] NativeWorkflowSegments = ["Klacks.Api", ".github", "workflows", "native-windows.yml"];
    private static readonly string[] TestsWorkflowSegments = ["Klacks.Api", ".github", "workflows", "tests.yml"];
    private static readonly string[] ArtifactWorkflowSegments = ["Klacks.Api", ".github", "workflows", "postgres-engine-artifact.yml"];
    private static readonly string[] DeployWorkflowSegments = ["Klacks.Api", ".github", "workflows", "deploy.yml"];
    private static readonly string[] Arm64WorkflowSegments = ["Klacks.Api", ".github", "workflows", "postgres-engine-artifact-arm64.yml"];
    private static readonly string[] Arm64PinSegments = ["Klacks.Api", "deploy", "postgres", "win-arm64", "pins.json"];
    private static readonly string[] KnownRids = [WinX64Rid, WinArm64Rid];
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
    public void NativeRids_AreKnownAndPublishUnderDistinctReleaseTags()
    {
        var pin = ReadPin();

        pin.NativeReleaseSuffixes.Keys.ShouldContain(WinX64Rid);
        pin.NativeReleaseSuffixes.Keys.ShouldAllBe(rid => KnownRids.Contains(rid));
        foreach (var (rid, suffix) in pin.NativeReleaseSuffixes)
        {
            suffix.ShouldMatch(ReleaseSuffixPattern, $"native.{rid}.releaseSuffix");
        }

        var tags = pin.NativeReleaseSuffixes.Values.Select(suffix => $"postgres-engine-{pin.Version}-pgvector{pin.Pgvector}{suffix}").ToList();
        tags.Distinct(StringComparer.Ordinal).Count().ShouldBe(tags.Count, "Every native RID needs its own release tag (releaseSuffix).");
    }

    [Test]
    public void Arm64BuildPins_MatchTheEnginePin()
    {
        var pin = ReadPin();
        using var engine = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(PinSegments)));
        var arm64 = engine.RootElement.GetProperty("native").GetProperty(WinArm64Rid);
        using var build = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(Arm64PinSegments)));
        var postgres = build.RootElement.GetProperty("postgresql");
        var pgvector = build.RootElement.GetProperty("pgvector");

        Text(arm64, "buildPins").ShouldBe(Arm64PinFileReference);
        Text(postgres, "version").ShouldBe(pin.Version);
        Text(postgres, "sha256").ShouldMatch(Sha256Pattern);
        Text(postgres, "sha256").ShouldBe(Text(arm64, "postgresSourceSha256"));
        Text(pgvector, "version").ShouldBe(pin.Pgvector);
        Text(pgvector, "tag").ShouldBe($"v{pin.Pgvector}");
        Text(pgvector, "commit").ShouldBe(Text(arm64, "pgvectorCommit"));
    }

    [Test]
    public void Arm64ArtifactWorkflow_RunsOnlyByHandAndReadsThePins()
    {
        var workflow = Read(RepositoryRootLocator.RequireFile(Arm64WorkflowSegments));

        workflow.ShouldContain(ManualTriggerOnly, customMessage: "The win-arm64 engine workflow must only have workflow_dispatch: a merge must not build or sign.");
        workflow.ShouldContain(PinFileReference);
        workflow.ShouldContain(Arm64PinFileReference);
        workflow.ShouldContain(Arm64Runner);
        workflow.ShouldContain(CodeSigningEnvironment);
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
        var native = root.GetProperty("native");
        var x64 = native.GetProperty(WinX64Rid);
        var suffixes = native.EnumerateObject().ToDictionary(
            rid => rid.Name,
            rid => rid.Value.TryGetProperty("releaseSuffix", out var suffix) ? suffix.GetString() ?? string.Empty : string.Empty,
            StringComparer.Ordinal);

        return new EnginePin(
            root.GetProperty("major").GetInt32(),
            root.GetProperty("version").GetString() ?? string.Empty,
            root.GetProperty("pgvector").GetString() ?? string.Empty,
            root.GetProperty("image").GetString() ?? string.Empty,
            x64.GetProperty("windowsBinariesUrl").GetString() ?? string.Empty,
            x64.GetProperty("windowsBinariesSha256").GetString() ?? string.Empty,
            suffixes[WinX64Rid],
            suffixes);
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? string.Empty;

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings(UnixLineEnding);

    private sealed record EnginePin(
        int Major,
        string Version,
        string Pgvector,
        string Image,
        string WindowsBinariesUrl,
        string WindowsBinariesSha256,
        string ReleaseSuffix,
        IReadOnlyDictionary<string, string> NativeReleaseSuffixes);
}
