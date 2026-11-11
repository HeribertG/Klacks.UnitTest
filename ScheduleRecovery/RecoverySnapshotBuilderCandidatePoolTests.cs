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
/// The borrowable candidates the recovery engine gets are capped: up to the cap all of them stay, beyond it the ones
/// with the lowest ids. Who is a candidate at all is pinned in ReplacementCandidatePoolResolverTests.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderCandidatePoolTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Test]
    public void UpToTheCap_EveryBorrowableCandidateStays()
    {
        // Arrange
        var people = People(RecoveryBuildConstants.CrossGroupPoolCap);

        // Act
        var capped = RecoverySnapshotBuilder.CapCrossGroup(people, GroupId, _logger);

        // Assert
        capped.Select(c => c.Id).ShouldBe(people.Select(c => c.Id));
    }

    [Test]
    public void BeyondTheCap_TheLowestIdsStay()
    {
        // Arrange
        const int surplus = 20;
        var people = People(RecoveryBuildConstants.CrossGroupPoolCap + surplus);

        // Act
        var capped = RecoverySnapshotBuilder.CapCrossGroup(people, GroupId, _logger);

        // Assert
        capped.Select(c => c.Id).ShouldBe(people.Select(c => c.Id).OrderBy(id => id).Take(RecoveryBuildConstants.CrossGroupPoolCap));
    }

    private static List<Client> People(int count)
        => Enumerable.Range(0, count)
            .Select(_ => new Client { Id = Guid.NewGuid(), Name = "Person", FirstName = string.Empty, Type = EntityTypeEnum.Employee })
            .ToList();
}
