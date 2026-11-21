// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Domain.Services.Update;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Update;

[TestFixture]
public class EngineUpgradeArtifactSelectorTests
{
    private const string HexDigest = "8c6038cf697f804008c81614aed4e3fcd375ec418888466bcd74c7e01bb11f6c";

    private static readonly InstallationIdentity Docker = new(InstallationKind.Docker, "linux-x64");
    private static readonly InstallationIdentity WindowsX64 = new(InstallationKind.Native, "win-x64");
    private static readonly InstallationIdentity WindowsArm64 = new(InstallationKind.Native, "win-arm64");

    private static readonly PostgresEngineDockerArtifact EngineImage = new()
    {
        Image = "ghcr.io/heribertg/klacks-postgres:19",
        Sha256 = "sha256:981ea51c18b99f66b59f91b48b5ee32006c494ed34dc973482f40904e48f42b2",
        Signature = "docker-engine-sig",
    };

    private static OnPremArtifact Bundle(string rid, string signature = "bundle-sig") => new()
    {
        Rid = rid,
        BundleUrl = $"https://example.invalid/klacks-postgres-19.0-{rid}.zip",
        Sha256 = HexDigest,
        Signature = signature,
    };

    private static PostgresEngineArtifact Engine(PostgresEngineDockerArtifact? docker, params OnPremArtifact[] native) => new()
    {
        Major = 19,
        Version = "19.0",
        Docker = docker,
        Native = native.ToList(),
    };

    [Test]
    public void Docker_TakesEngineImageVerbatimWithDigestPrefix()
    {
        var selected = EngineUpgradeArtifactSelector.Select(Engine(EngineImage, Bundle("win-x64")), Docker);

        selected.ShouldBe(new SelectedUpdateArtifact(EngineImage.Image, EngineImage.Sha256, EngineImage.Signature));
    }

    [Test]
    public void Docker_WithoutEngineImage_SelectsNothingEvenWhenNativeBundleExists()
    {
        EngineUpgradeArtifactSelector.Select(Engine(null, Bundle("linux-x64")), Docker).ShouldBeNull();
    }

    [Test]
    public void Docker_WithoutSignature_SelectsNothing()
    {
        var unsigned = new PostgresEngineDockerArtifact { Image = EngineImage.Image, Sha256 = EngineImage.Sha256 };

        EngineUpgradeArtifactSelector.Select(Engine(unsigned), Docker).ShouldBeNull();
    }

    [Test]
    public void Native_TakesBundleOfOwnRuntimeIdentifier()
    {
        var selected = EngineUpgradeArtifactSelector.Select(
            Engine(EngineImage, Bundle("win-arm64"), Bundle("win-x64")), WindowsX64);

        selected.ShouldBe(new SelectedUpdateArtifact(
            "https://example.invalid/klacks-postgres-19.0-win-x64.zip", HexDigest, "bundle-sig"));
    }

    [Test]
    public void Native_NeverFallsBackToDockerOrOtherPlatform()
    {
        EngineUpgradeArtifactSelector.Select(Engine(EngineImage, Bundle("win-x64")), WindowsArm64).ShouldBeNull();
    }

    [Test]
    public void Native_WithoutSignature_SelectsNothing()
    {
        EngineUpgradeArtifactSelector.Select(Engine(null, Bundle("win-x64", signature: "")), WindowsX64).ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("981ea51c18b99f66b59f91b48b5ee32006c494ed34dc973482f40904e48f42b2")]
    [TestCase("sha256:981EA51C18B99F66B59F91B48B5EE32006C494ED34DC973482F40904E48F42B2")]
    [TestCase("sha256:981ea51c")]
    [TestCase("sha256:981ea51c18b99f66b59f91b48b5ee32006c494ed34dc973482f40904e48f42b2\n")]
    public void Docker_MalformedDigest_SelectsNothing(string sha256)
    {
        var image = new PostgresEngineDockerArtifact { Image = EngineImage.Image, Sha256 = sha256, Signature = EngineImage.Signature };

        EngineUpgradeArtifactSelector.Select(Engine(image), Docker).ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("sha256:8c6038cf697f804008c81614aed4e3fcd375ec418888466bcd74c7e01bb11f6c")]
    [TestCase("8C6038CF697F804008C81614AED4E3FCD375EC418888466BCD74C7E01BB11F6C")]
    [TestCase("8c6038cf")]
    public void Native_MalformedHash_SelectsNothing(string sha256)
    {
        var bundle = Bundle("win-x64");
        bundle.Sha256 = sha256;

        EngineUpgradeArtifactSelector.Select(Engine(null, bundle), WindowsX64).ShouldBeNull();
    }

    [TestCase("ghcr.io/heribertg/klacks-postgres:19 extra")]
    [TestCase("ghcr.io/heribertg/klacks-postgres:19\u0007")]
    public void Docker_MalformedReference_SelectsNothing(string image)
    {
        var docker = new PostgresEngineDockerArtifact { Image = image, Sha256 = EngineImage.Sha256, Signature = EngineImage.Signature };

        EngineUpgradeArtifactSelector.Select(Engine(docker), Docker).ShouldBeNull();
    }

    [Test]
    public void SignatureWithControlCharacter_SelectsNothing()
    {
        var docker = new PostgresEngineDockerArtifact { Image = EngineImage.Image, Sha256 = EngineImage.Sha256, Signature = "abc\ndef" };

        EngineUpgradeArtifactSelector.Select(Engine(docker), Docker).ShouldBeNull();
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1000)]
    public void MajorOutsideOneToThreeDigits_SelectsNothing(int major)
    {
        var engine = Engine(EngineImage);
        engine.Major = major;

        EngineUpgradeArtifactSelector.Select(engine, Docker).ShouldBeNull();
    }

    [Test]
    public void NoEngine_SelectsNothing()
    {
        EngineUpgradeArtifactSelector.Select(null, Docker).ShouldBeNull();
    }
}
