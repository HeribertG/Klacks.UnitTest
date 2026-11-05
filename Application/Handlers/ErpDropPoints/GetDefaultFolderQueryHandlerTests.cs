// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GetDefaultFolderQueryHandler: the Import settings card gets the native folder as is, the host folder
/// with the container path alongside on Docker when the compose file reports it, and the container path otherwise.
/// </summary>

using Klacks.Api.Application.Handlers.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Domain.Services.Imports;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Application.Handlers.ErpDropPoints;

[TestFixture]
public class GetDefaultFolderQueryHandlerTests
{
    private const string BucketPrefix = "erp/orders";
    private const string NormalizedPrefix = "erp/orders/";
    private const string NativeFolder = @"C:\ProgramData\Klacks\ErpImport\erp\orders\";
    private const string ContainerFolder = "/app/ErpImport/erp/orders/";
    private const string HostHint = "/opt/klacks/erp-import";

    private static GetDefaultFolderQueryHandler CreateHandler(InstallationKind kind, string resolvedPath, string hostPathHint = "")
    {
        var provider = Substitute.For<IErpDefaultDropPointProvider>();
        provider.GetOrCreateDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new ErpDropPoint { BucketPrefix = BucketPrefix });
        var storage = Substitute.For<IObjectStorageService>();
        storage.ResolvePath(NormalizedPrefix).Returns(resolvedPath);
        var installation = Substitute.For<IInstallationIdentityProvider>();
        installation.Get().Returns(new InstallationIdentity(kind, kind == InstallationKind.Native ? "win-x64" : "linux-x64"));
        var options = Options.Create(new ErpObjectStorageOptions { HostPathHint = hostPathHint });
        return new GetDefaultFolderQueryHandler(provider, new ErpDropFolderResolver(storage, installation, options));
    }

    [Test]
    public async Task Handle_Native_ReturnsTheAbsoluteFolderWithoutContainerPath()
    {
        var result = await CreateHandler(InstallationKind.Native, NativeFolder, HostHint)
            .Handle(new GetDefaultFolderQuery(), CancellationToken.None);

        result.Installation.ShouldBe(ErpHandoverInstallations.Native);
        result.FolderPath.ShouldBe(NativeFolder);
        result.ContainerPath.ShouldBeNull();
        result.IsHostPath.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_DockerWithHostPathHint_ReturnsHostFolderAndContainerPath()
    {
        var result = await CreateHandler(InstallationKind.Docker, ContainerFolder, HostHint)
            .Handle(new GetDefaultFolderQuery(), CancellationToken.None);

        result.Installation.ShouldBe(ErpHandoverInstallations.Docker);
        result.FolderPath.ShouldBe("/opt/klacks/erp-import/erp/orders");
        result.ContainerPath.ShouldBe(ContainerFolder);
        result.IsHostPath.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_DockerWithoutHostPathHint_ReturnsTheContainerPath()
    {
        var result = await CreateHandler(InstallationKind.Docker, ContainerFolder)
            .Handle(new GetDefaultFolderQuery(), CancellationToken.None);

        result.FolderPath.ShouldBe(ContainerFolder);
        result.ContainerPath.ShouldBe(ContainerFolder);
        result.IsHostPath.ShouldBeFalse();
    }
}
