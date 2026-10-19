// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpImportInstanceIdentity: the id is "MachineName:ProcessGuid", unique per process
/// start, and always fits erp_import_file.claimed_by even for a 64-character Linux host name.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpImportInstanceIdentityTests
{
    private const int LinuxHostNameMaxLength = 64;

    [Test]
    public void InstanceId_IsMachineNameColonProcessGuid()
    {
        var processGuid = Guid.NewGuid();

        var identity = new ErpImportInstanceIdentity("api-node-1", processGuid);

        identity.InstanceId.ShouldBe($"api-node-1:{processGuid:N}");
    }

    [Test]
    public void DefaultIdentity_UsesTheMachineName_AndDiffersPerInstance()
    {
        var first = new ErpImportInstanceIdentity();
        var second = new ErpImportInstanceIdentity();

        first.InstanceId.ShouldStartWith(Environment.MachineName + ":");
        first.InstanceId.ShouldNotBe(second.InstanceId);
    }

    [Test]
    public void InstanceId_ForTheLongestLinuxHostName_FitsTheClaimedByColumn()
    {
        var processGuid = Guid.NewGuid();
        var longHostName = new string('h', LinuxHostNameMaxLength);

        var identity = new ErpImportInstanceIdentity(longHostName, processGuid);

        identity.InstanceId.Length.ShouldBeLessThanOrEqualTo(ErpImportFileLimits.ClaimedByMaxLength);
        identity.InstanceId.ShouldEndWith($":{processGuid:N}");
    }

    [Test]
    public void InstanceId_ForAnOversizedMachineName_IsTruncatedButKeepsTheProcessGuid()
    {
        var processGuid = Guid.NewGuid();
        var oversized = new string('x', ErpImportFileLimits.ClaimedByMaxLength * 2);

        var identity = new ErpImportInstanceIdentity(oversized, processGuid);

        identity.InstanceId.Length.ShouldBe(ErpImportFileLimits.ClaimedByMaxLength);
        identity.InstanceId.ShouldEndWith($":{processGuid:N}");
    }
}
