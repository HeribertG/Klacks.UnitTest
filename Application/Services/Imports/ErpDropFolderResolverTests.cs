// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpDropFolderResolver: the folder is resolved from the normalized bucket prefix through the object
/// storage, the host path hint is applied on Docker only, and the advice matches the resolved paths.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Domain.Services.Imports;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpDropFolderResolverTests
{
    private const string ContainerFolder = "/app/ErpImport/erp/orders/";
    private const string HostHint = "/opt/klacks/erp-import";
    private const string HostFolder = "/opt/klacks/erp-import/erp/orders";

    private static ErpDropFolderResolver CreateResolver(InstallationKind kind, string hostPathHint, IObjectStorageService storage)
    {
        var installation = Substitute.For<IInstallationIdentityProvider>();
        installation.Get().Returns(new InstallationIdentity(kind, "linux-x64"));
        var options = Options.Create(new ErpObjectStorageOptions { HostPathHint = hostPathHint });
        return new ErpDropFolderResolver(storage, installation, options);
    }

    private static IObjectStorageService Storage()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.ResolvePath("erp/orders/").Returns(ContainerFolder);
        return storage;
    }

    [Test]
    public void Resolve_DockerWithHint_ReturnsHostFolderAndHostAdvice()
    {
        var storage = Storage();

        var folder = CreateResolver(InstallationKind.Docker, HostHint, storage).Resolve("erp/orders");

        storage.Received(1).ResolvePath("erp/orders/");
        folder.FolderPath.ShouldBe(ContainerFolder);
        folder.HostFolderPath.ShouldBe(HostFolder);
        folder.Guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.GrantWriteAccessOnHostFolder);
        folder.Guidance.Advice.ShouldContain(HostFolder);
    }

    [Test]
    public void Resolve_DockerWithoutHint_ReturnsNoHostFolder()
    {
        var folder = CreateResolver(InstallationKind.Docker, string.Empty, Storage()).Resolve("erp/orders/");

        folder.HostFolderPath.ShouldBeNull();
        folder.Guidance.FolderDropRequirement.ShouldBe(ErpFolderDropRequirements.MountHostFolder);
    }

    [Test]
    public void Resolve_Native_IgnoresTheHint()
    {
        var folder = CreateResolver(InstallationKind.Native, HostHint, Storage()).Resolve("erp/orders");

        folder.HostFolderPath.ShouldBeNull();
        folder.Guidance.Installation.ShouldBe(ErpHandoverInstallations.Native);
    }
}
