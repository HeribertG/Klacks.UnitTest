// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for check_erp_drop_point_folder_health: a healthy folder reports itself as healthy, a Docker installation
/// names the host folder bound onto the storage root when the compose file reports it (and says so when it does not)
/// plus the standard bind mount, and a native installation gets neither the container wording nor the compose example
/// but the reminder that the ERP account needs its own write access.
/// </summary>

using Klacks.Api.Application.DTOs.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CheckErpDropPointFolderHealthSkillTests
{
    private const string Prefix = "erp/orders/";
    private const string ResolvedPath = @"C:\ProgramData\Klacks\ErpImport\erp\orders\";

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    private static CheckErpDropPointFolderHealthSkill CreateSkill(InstallationKind kind, bool writable = true, string hostPathHint = "")
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { BucketPrefix = "erp/orders", IsEnabled = true });
        var objectStorage = Substitute.For<IObjectStorageService>();
        objectStorage.ResolvePath(Prefix).Returns(ResolvedPath);
        objectStorage.CheckHealthAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectStorageHealthResult(
                ResolvedPath,
                true,
                true,
                writable,
                writable ? null : "access denied",
                [new ObjectStoragePrefixHealth(Prefix, true, null)]));
        var installation = Substitute.For<IInstallationIdentityProvider>();
        installation.Get().Returns(new InstallationIdentity(kind, kind == InstallationKind.Native ? "win-x64" : "linux-x64"));
        var options = Options.Create(new ErpObjectStorageOptions { HostPathHint = hostPathHint });
        return new CheckErpDropPointFolderHealthSkill(mediator, objectStorage, new ErpDropFolderResolver(objectStorage, installation, options));
    }

    [Test]
    public async Task ExecuteAsync_DockerWithoutHostPathHint_SaysTheHostFolderIsUnknownAndNamesTheBindMount()
    {
        var result = await CreateSkill(InstallationKind.Docker).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("does not report which host folder is mounted");
        result.Message.ShouldContain("${KLACKS_ERP_IMPORT_HOST_DIR:-./erp-import}:/app/ErpImport");
        result.Message.ShouldNotContain("api-erp-import");
    }

    [Test]
    public async Task ExecuteAsync_DockerWithHostPathHint_NamesTheHostDropFolder()
    {
        var result = await CreateSkill(InstallationKind.Docker, hostPathHint: "/opt/klacks/erp-import")
            .ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("On the host this is the folder '/opt/klacks/erp-import/erp/orders'");
        result.Message.ShouldNotContain("does not report");
    }

    [Test]
    public async Task ExecuteAsync_NativeInstallation_LeavesOutTheContainerRemarkAndNamesTheErpAccount()
    {
        var result = await CreateSkill(InstallationKind.Native).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotContain("container");
        result.Message.ShouldNotContain("docker-compose");
        result.Message.ShouldContain("the account the ERP export runs under");
        result.Message.ShouldContain("get_erp_drop_point_settings");
    }

    [Test]
    public async Task ExecuteAsync_NotWritable_ReportsTheWriteErrorWithoutContainerWording()
    {
        var result = await CreateSkill(InstallationKind.Native, writable: false).ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain("access denied");
        result.Message.ShouldNotContain("container");
    }
}
