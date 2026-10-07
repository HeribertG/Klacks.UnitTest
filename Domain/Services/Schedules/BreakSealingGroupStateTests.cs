// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The sealing group of a break (Break.SealedByGroupId) is server-owned seal state: a full-row PUT must carry it
/// over unchanged, and a single-entry lock change supersedes the period seal and clears it.
/// </summary>

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class BreakSealingGroupStateTests
{
    [Test]
    public void CarryOver_KeepsTheSealingGroupOfTheStoredBreak()
    {
        var groupId = Guid.NewGuid();
        var stored = new Break { LockLevel = WorkLockLevel.Closed, SealedByGroupId = groupId };
        var rebuilt = new Break { SealedByGroupId = Guid.NewGuid() };

        ScheduleEntrySealState.CarryOver(rebuilt, stored);

        rebuilt.SealedByGroupId.ShouldBe(groupId);
        rebuilt.LockLevel.ShouldBe(WorkLockLevel.Closed);
    }

    [Test]
    public void CarryOver_WithoutStoredRow_LeavesNoSealingGroup()
    {
        var rebuilt = new Break { SealedByGroupId = Guid.NewGuid() };

        ScheduleEntrySealState.CarryOver(rebuilt, null);

        rebuilt.SealedByGroupId.ShouldBeNull();
    }

    [Test]
    public void Unseal_ByAdmin_ClearsTheSealingGroup()
    {
        var entry = new Break { LockLevel = WorkLockLevel.Closed, SealedByGroupId = Guid.NewGuid() };

        new WorkLockLevelService().Unseal(entry, isAdmin: true, isAuthorised: false);

        entry.LockLevel.ShouldBe(WorkLockLevel.None);
        entry.SealedByGroupId.ShouldBeNull();
    }
}
