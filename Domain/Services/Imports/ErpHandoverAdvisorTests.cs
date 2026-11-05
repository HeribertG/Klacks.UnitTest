// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpHandoverAdvisor: a native Windows installation prefers the folder and names the grant on
/// exactly that folder, a Docker installation without a reported host folder prefers push and upload and points at the
/// standard bind mount, a Docker installation with a host folder prefers the folder and names the host path, the Docker
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
    private const string HostFolder = "/opt/klacks/erp-import/erp/orders";
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
    public void Advise_NativeWindows_CallsTheInstallationAWindowsOfficePcAndNeverNative()
    {
        var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(InstallationKind.Native, WindowsRuntime), WindowsFolder);

        guidance.InstallationLabel.ShouldBe("Windows office PC");
        guidance.Advice.ShouldStartWith("Windows office PC installation");
        System.Text.RegularExpressions.Regex.IsMatch(guidance.Advice, @"\bnative(ly)?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).ShouldBeFalse();
    }

    [Test]
    public void Advise_NativeNonWindows_CallsTheInstallationAnOfficePc()
    {
        var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(InstallationKind.Native, MacRuntime), "/var/klacks/erp/orders");

        guidance.InstallationLabel.ShouldBe("office PC");
    }

    [Test]
    public void Advise_Docker_CallsTheInstallationADockerServer()
    {
        var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(InstallationKind.Docker, LinuxRuntime), DockerFolder, HostFolder);

        guidance.InstallationLabel.ShouldBe("Docker server");
        guidance.Advice.ShouldStartWith("Docker server installation");
    }

    [Test]
    public void AnswerGuide_AsksForThreeWaysTheInstallationRecommendationTheWritePermissionAndTheNavigationOffer()
    {
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("three ways");
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("folder drop");
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("HTTPS push");
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("upload");
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("THIS installation");
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("write permission");
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("offer to open Settings > Import");
        ErpHandoverAdvisor.AnswerGuide.ShouldContain("InstallationLabel");
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
    public void Advise_DockerWithoutHostFolder_PrefersPushAndUploadAndNamesTheHostFolderMount()
    {
        var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(InstallationKind.Docker, LinuxRuntime), DockerFolder);

        guidance.Installation.ShouldBe("docker");
        guidance.RecommendedChannels.ShouldBe(
            [ErpHandoverChannels.HttpsPush, ErpHandoverChannels.AdminUpload, ErpHandoverChannels.FolderDrop]);
        guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.MountHostFolder);
        guidance.Advice.ShouldContain(DockerFolder);
        guidance.Advice.ShouldContain("does not report which server (host) folder is mounted onto /app/ErpImport");
        guidance.Advice.ShouldContain("./erp-import");
        guidance.Advice.ShouldNotContain("named volume");
        guidance.Advice.ShouldContain("hosts this installation");
    }

    [Test]
    public void Advise_DockerWithHostFolder_PrefersTheFolderAndNamesBothPaths()
    {
        var guidance = ErpHandoverAdvisor.Advise(
            new InstallationIdentity(InstallationKind.Docker, LinuxRuntime), DockerFolder, HostFolder);

        guidance.Installation.ShouldBe("docker");
        guidance.RecommendedChannels.ShouldBe(
            [ErpHandoverChannels.FolderDrop, ErpHandoverChannels.HttpsPush, ErpHandoverChannels.AdminUpload]);
        guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.GrantWriteAccessOnHostFolder);
        guidance.Advice.ShouldContain($"server (host) folder '{HostFolder}'");
        guidance.Advice.ShouldContain(DockerFolder);
        guidance.Advice.ShouldContain("hosts this installation");
    }

    [Test]
    public void Advise_DockerWithBlankHostFolder_FallsBackToTheContainerAdvice()
    {
        var guidance = ErpHandoverAdvisor.Advise(
            new InstallationIdentity(InstallationKind.Docker, LinuxRuntime), DockerFolder, "  ");

        guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.MountHostFolder);
    }

    [Test]
    public void Advise_NativeWithHostFolder_IgnoresIt()
    {
        var guidance = ErpHandoverAdvisor.Advise(
            new InstallationIdentity(InstallationKind.Native, WindowsRuntime), WindowsFolder, HostFolder);

        guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.GrantWriteAccessOnFolder);
        guidance.Advice.ShouldNotContain(HostFolder);
    }

    [Test]
    public void Advise_EveryChannelAppearsExactlyOnce()
    {
        foreach (var kind in new[] { InstallationKind.Native, InstallationKind.Docker })
        {
            foreach (var hostFolder in new[] { null, HostFolder })
            {
                var guidance = ErpHandoverAdvisor.Advise(new InstallationIdentity(kind, WindowsRuntime), WindowsFolder, hostFolder);

                guidance.RecommendedChannels.Count.ShouldBe(ChannelCount);
                guidance.RecommendedChannels.Distinct().Count().ShouldBe(ChannelCount);
            }
        }
    }
}
