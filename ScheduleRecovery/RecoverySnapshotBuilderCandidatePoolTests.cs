// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// The candidate pool of the recovery holds only people who can take over a replacement: a customer under the root is
/// neither a candidate nor counted against the cap of cross-group candidates.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderCandidatePoolTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Test]
    public void Customers_AreNotCandidates_InGroupOrAcrossGroups()
    {
        var inGroupEmployee = Client(EntityTypeEnum.Employee);
        var inGroupCustomer = Client(EntityTypeEnum.Customer);
        var crossGroupExternal = Client(EntityTypeEnum.ExternEmp);
        var crossGroupCustomer = Client(EntityTypeEnum.Customer);
        var inGroup = new HashSet<Guid> { inGroupEmployee.Id, inGroupCustomer.Id };

        var pool = RecoverySnapshotBuilder.SelectCandidatePool(
            [inGroupEmployee, inGroupCustomer, crossGroupExternal, crossGroupCustomer], inGroup, GroupId, _logger);

        pool.Select(c => c.Id).ShouldBe([inGroupEmployee.Id, crossGroupExternal.Id], ignoreOrder: true);
    }

    [Test]
    public void Customers_DoNotUseUpTheCrossGroupCap()
    {
        var people = Enumerable.Range(0, RecoveryBuildConstants.CrossGroupPoolCap)
            .Select(_ => Client(EntityTypeEnum.Employee))
            .ToList();
        var customers = Enumerable.Range(0, 20).Select(_ => Client(EntityTypeEnum.Customer)).ToList();

        var pool = RecoverySnapshotBuilder.SelectCandidatePool(
            people.Concat(customers).ToList(), new HashSet<Guid>(), GroupId, _logger);

        pool.Count.ShouldBe(RecoveryBuildConstants.CrossGroupPoolCap);
        pool.ShouldAllBe(c => c.Type == EntityTypeEnum.Employee);
    }

    private static Client Client(EntityTypeEnum type)
        => new() { Id = Guid.NewGuid(), Name = type.ToString(), FirstName = string.Empty, Type = type };
}
