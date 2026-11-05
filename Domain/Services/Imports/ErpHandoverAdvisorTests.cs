// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpHandoverAdvisor: a native Windows installation prefers the folder and names the grant on
/// exactly that folder, a Docker installation prefers push and upload and names the host-folder mount, the Docker
/// advice covers both an own and a vendor-hosted server, and the path the caller passes appears in the advice.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Domain.Services.Imports;

namespace Klacks.UnitTest.Domain.Services.Imports;

[TestFixture]
public class ErpHandoverAdvisorTests
{
    private const string WindowsRuntime = "win-x64";
    private const string LinuxRuntime = "linux-x64";
    private const string MacRuntime = "osx-arm64";
    private const string WindowsFolder = @"C:\ProgramData\Klacks\ErpImport\erp\orders\";
    private const string DockerFolder = "/app/ErpImport/erp/orders/";
    private const int ChannelCount = 3;

    [Test]
    public void Advise_NativeWindows_PrefersTheFolderAndNamesTheGrantOnExactlyThatFolder()
    {
        var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(InstallationKind.Native, WindowsRuntime), WindowsFolder);

        guidance.Installation.ShouldBe("native");
        guidance.RecommendedChannels.ShouldBe(
            [ErpHandoverChannels.FolderDrop, ErpHandoverChannels.HttpsPush, ErpHandoverChannels.AdminUpload]);
        guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.GrantWriteAccessOnFolder);
        guidance.Advice.ShouldContain(WindowsFolder);
        guidance.Advice.ShouldContain("SYSTEM and Administrators");
        guidance.Advice.ShouldContain("Modify");
        guidance.Advice.ShouldContain("exactly this folder");
        guidance.Advice.ShouldContain("not on the whole Klacks data folder");
    }

    [Test]
    public void Advise_NativeNonWindows_DoesNotClaimWindowsRights()
    {
        var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(InstallationKind.Native, MacRuntime), "/var/klacks/erp/orders/");

        guidance.RecommendedChannels[0].ShouldBe(ErpHandoverChannels.FolderDrop);
        guidance.Advice.ShouldNotContain("SYSTEM and Administrators");
        guidance.Advice.ShouldNotContain("Windows account");
    }

    [Test]
    public void Advise_Docker_PrefersPushAndUploadAndNamesTheHostFolderMount()
    {
        var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(InstallationKind.Docker, LinuxRuntime), DockerFolder);

        guidance.Installation.ShouldBe("docker");
        guidance.RecommendedChannels.ShouldBe(
            [ErpHandoverChannels.HttpsPush, ErpHandoverChannels.AdminUpload, ErpHandoverChannels.FolderDrop]);
        guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.MountHostFolder);
        guidance.Advice.ShouldContain(DockerFolder);
        guidance.Advice.ShouldContain("mounts a host folder onto /app/ErpImport");
        guidance.Advice.ShouldContain("hosts this installation");
    }

    [Test]
    public void Advise_EveryChannelAppearsExactlyOnce()
    {
        foreach (var kind in new[] { InstallationKind.Native, InstallationKind.Docker })
        {
            var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(kind, WindowsRuntime), WindowsFolder);

            guidance.RecommendedChannels.Count.ShouldBe(ChannelCount);
            guidance.RecommendedChannels.Distinct().Count().ShouldBe(ChannelCount);
        }
    }
}
