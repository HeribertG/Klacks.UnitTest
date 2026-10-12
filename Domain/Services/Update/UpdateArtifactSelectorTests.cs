using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Domain.Services.Update;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Update;

[TestFixture]
public class UpdateArtifactSelectorTests
{
    private static readonly InstallationIdentity Docker = new(InstallationKind.Docker, "linux-arm64");
    private static readonly InstallationIdentity WindowsX64 = new(InstallationKind.Native, "win-x64");

    private static OnPremArtifact Bundle(string rid, string signature = "bundle-sig") => new()
    {
        Rid = rid,
        BundleUrl = $"https://example.invalid/klacks-{rid}.zip",
        Sha256 = $"hash-{rid}",
        Signature = signature,
    };

    private static UpdateManifest Manifest(DockerArtifact? docker, params OnPremArtifact[] native) => new()
    {
        LatestVersion = "1.2.3",
        Signature = "docker-sig",
        Artifacts = new UpdateArtifacts { Docker = docker, Native = native.ToList() },
    };

    private static readonly DockerArtifact DockerImages = new()
    {
        ApiImage = "ghcr.io/heribertg/klacks-api:1.2.3",
        UiImage = "ghcr.io/heribertg/klacks-ui:1.2.3",
        Sha256 = "sha256:digest",
    };

    [Test]
    public void Docker_TakesDockerImagesWithManifestSignature()
    {
        var selected = UpdateArtifactSelector.Select(Manifest(DockerImages, Bundle("win-x64")), Docker);

        selected.ShouldBe(new SelectedUpdateArtifact(DockerImages.ApiImage, DockerImages.Sha256, "docker-sig"));
    }

    [Test]
    public void Docker_WithoutDockerArtifact_SelectsNothing()
    {
        UpdateArtifactSelector.Select(Manifest(null, Bundle("linux-arm64")), Docker).ShouldBeNull();
    }

    [Test]
    public void Native_TakesOwnRuntimeIdentifierWithBundleSignature()
    {
        var manifest = Manifest(DockerImages, Bundle("win-arm64"), Bundle("win-x64"), Bundle("osx-arm64"));

        var selected = UpdateArtifactSelector.Select(manifest, WindowsX64);

        selected.ShouldBe(new SelectedUpdateArtifact("https://example.invalid/klacks-win-x64.zip", "hash-win-x64", "bundle-sig"));
    }

    [Test]
    public void Native_NeverFallsBackToDockerOrOtherPlatform()
    {
        var manifest = Manifest(DockerImages, Bundle("win-arm64"), Bundle("osx-arm64"));

        UpdateArtifactSelector.Select(manifest, WindowsX64).ShouldBeNull();
    }

    [Test]
    public void Native_UnsignedBundle_IsNotSelected()
    {
        UpdateArtifactSelector.Select(Manifest(null, Bundle("win-x64", signature: "")), WindowsX64).ShouldBeNull();
    }

    [Test]
    public void Native_ReadsLegacyOnPremWindowsList()
    {
        var manifest = Manifest(null);
        manifest.Artifacts.OnPremWindows.Add(Bundle("win-x64"));

        UpdateArtifactSelector.Select(manifest, WindowsX64).ShouldNotBeNull();
    }
}
