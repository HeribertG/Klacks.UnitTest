// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the pinned Klacks.Stt version. The single source is Klacks.Api/deploy/stt-version (content X.Y.Z). The
/// default tag of the klacks-stt image in every compose file (server, on-prem, the marketplace mirror of the on-prem
/// bundle) must equal it, the klacks-stt service block must be identical in all of them, and the native Windows workflow
/// must check out Klacks.Stt at the same version, so a Docker customer and a native customer never run different
/// speech services from the same release. A floating :latest or :edge would also pull untested main builds. The Api
/// image carries the pin as label org.klacks.stt-version, from which the on-prem updater moves KLACKS_STT_TAG.
///
/// Klacks.Marketplace is a sibling repository; where it is not checked out its mirror is skipped, the same split
/// OnPremTemplateMarketplaceMirrorGuardTests makes.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SttImagePinGuardTests
{
    private const string SttServiceHeader = "  klacks-stt:\n";
    private const string ImageReferencePrefix = "image: ghcr.io/heribertg/klacks-stt:";
    private const string UnixLineEnding = "\n";
    private const string BlockEnd = "\n\n";
    private const string VersionPattern = @"^\d+\.\d+\.\d+$";
    private const string WorkflowVersionStep = "deploy/stt-version";
    private const string WorkflowCheckoutRef = "ref: v${{ steps.sttver.outputs.version }}";
    private const string ImageBuildArg = "KLACKS_STT_VERSION=${{ steps.sttver.outputs.version }}";
    private const string DockerfileLabel = "LABEL org.klacks.stt-version=\"${KLACKS_STT_VERSION}\"";

    private static readonly string[] VersionFileSegments = ["Klacks.Api", "deploy", "stt-version"];
    private static readonly string[] ServerComposeSegments = ["Klacks.Api", "deploy", "docker-compose-server.yml"];
    private static readonly string[] OnPremComposeSegments = ["Klacks.Api", "deploy", "onprem", "docker-compose.yml"];
    private static readonly string[] MarketplaceComposeSegments =
        ["Klacks.Marketplace", "Resources", "OnPremTemplate", "docker-compose.yml"];
    private static readonly string[] NativeWorkflowSegments = ["Klacks.Api", ".github", "workflows", "native-windows.yml"];
    private static readonly string[] DeployWorkflowSegments = ["Klacks.Api", ".github", "workflows", "deploy.yml"];
    private static readonly string[] DockerfileSegments = ["Klacks.Api", "Dockerfile"];

    [Test]
    public void SttVersionFile_HoldsASemanticVersion()
    {
        ReadPinnedVersion().ShouldMatch(VersionPattern);
    }

    [Test]
    public void EveryComposeFile_DefaultsTheSttImageToThePinnedVersion()
    {
        var version = ReadPinnedVersion();
        var expectedImage = $"{ImageReferencePrefix}${{KLACKS_STT_TAG:-{version}}}";

        foreach (var (name, content) in ReadComposeFiles())
        {
            var imageLines = content.Split(UnixLineEnding)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith(ImageReferencePrefix, StringComparison.Ordinal))
                .ToList();

            imageLines.ShouldBe([expectedImage], $"{name} must pin the klacks-stt image to deploy/stt-version ({version}).");
        }
    }

    [Test]
    public void KlacksSttServiceBlock_IsIdenticalInEveryComposeFile()
    {
        var blocks = ReadComposeFiles()
            .Select(file => (file.Name, Block: ExtractServiceBlock(file.Name, file.Content)))
            .ToList();

        foreach (var (name, block) in blocks.Skip(1))
        {
            block.ShouldBe(blocks[0].Block, $"The klacks-stt service block of {name} differs from {blocks[0].Name}.");
        }
    }

    [Test]
    public void KlacksSttServiceBlock_IsHardened()
    {
        foreach (var (name, content) in ReadComposeFiles())
        {
            var block = ExtractServiceBlock(name, content);

            block.ShouldContain("security_opt: [\"no-new-privileges:true\"]", customMessage: name);
            block.ShouldContain("cap_drop: [ALL]", customMessage: name);
            block.ShouldContain("AllowedHosts=klacks-stt;localhost;127.0.0.1", customMessage: name);
        }
    }

    [Test]
    public void NativeWindowsWorkflow_ChecksOutKlacksSttAtThePinnedVersionFile()
    {
        var workflow = Normalize(File.ReadAllText(RepositoryRootLocator.RequireFile(NativeWorkflowSegments)));

        workflow.ShouldContain(WorkflowVersionStep);
        workflow.ShouldContain(WorkflowCheckoutRef);
    }

    [Test]
    public void ApiImage_CarriesThePinnedSttVersionAsLabelForTheUpdater()
    {
        var workflow = Read(RepositoryRootLocator.RequireFile(DeployWorkflowSegments));
        var dockerfile = Read(RepositoryRootLocator.RequireFile(DockerfileSegments));

        workflow.ShouldContain(WorkflowVersionStep);
        workflow.ShouldContain(ImageBuildArg);
        dockerfile.ShouldContain(DockerfileLabel);
    }

    private static string ReadPinnedVersion() =>
        Normalize(File.ReadAllText(RepositoryRootLocator.RequireFile(VersionFileSegments))).Trim();

    private static List<(string Name, string Content)> ReadComposeFiles()
    {
        var files = new List<(string, string)>
        {
            (string.Join('/', ServerComposeSegments), Read(RepositoryRootLocator.RequireFile(ServerComposeSegments))),
            (string.Join('/', OnPremComposeSegments), Read(RepositoryRootLocator.RequireFile(OnPremComposeSegments))),
        };

        var marketplace = RepositoryRootLocator.FindFile(MarketplaceComposeSegments);
        if (marketplace != null)
        {
            files.Add((string.Join('/', MarketplaceComposeSegments), Read(marketplace)));
        }

        return files;
    }

    private static string ExtractServiceBlock(string name, string content)
    {
        var start = content.IndexOf(SttServiceHeader, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{name} has no klacks-stt service.");

        var end = content.IndexOf(BlockEnd, start, StringComparison.Ordinal);
        return end < 0 ? content[start..] : content[start..end];
    }

    private static string Read(string path) => Normalize(File.ReadAllText(path));

    private static string Normalize(string text) => text.ReplaceLineEndings(UnixLineEnding);
}
