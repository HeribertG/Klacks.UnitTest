// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Rule table of the parent-Work lock for child entries (expenses): None open for all, Confirmed open for all,
/// Approved only for Admin/Authorised, Closed for nobody (admins included - the legacy period close writes no
/// SealedDay rows, so the day lock would not stop them), scenario Works never checked.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class ParentWorkLockGuardTests
{
    private ParentWorkLockGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _guard = new ParentWorkLockGuard(new WorkLockLevelService());
    }

    [TestCase(WorkLockLevel.None, false, false)]
    [TestCase(WorkLockLevel.None, false, true)]
    [TestCase(WorkLockLevel.None, true, false)]
    [TestCase(WorkLockLevel.Confirmed, false, false)]
    [TestCase(WorkLockLevel.Confirmed, false, true)]
    [TestCase(WorkLockLevel.Confirmed, true, false)]
    [TestCase(WorkLockLevel.Approved, false, true)]
    [TestCase(WorkLockLevel.Approved, true, false)]
    public void EnsureChildWritable_Allowed(WorkLockLevel level, bool isAdmin, bool isAuthorised)
    {
        Should.NotThrow(() => _guard.EnsureChildWritable(NewWork(level, null), isAdmin, isAuthorised));
    }

    [Test]
    public void EnsureChildWritable_ApprovedParent_WithoutRole_IsRefused()
    {
        var ex = Should.Throw<InvalidRequestException>(
            () => _guard.EnsureChildWritable(NewWork(WorkLockLevel.Approved, null), false, false));

        ex.Message.ShouldBe(ParentWorkLockGuard.SealedParentMessage);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void EnsureChildWritable_ClosedParent_IsRefusedForEveryone(bool isAdmin, bool isAuthorised)
    {
        var ex = Should.Throw<InvalidRequestException>(
            () => _guard.EnsureChildWritable(NewWork(WorkLockLevel.Closed, null), isAdmin, isAuthorised));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedParentMessage);
    }

    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public void EnsureChildWritable_ScenarioParent_IsNeverChecked(WorkLockLevel level)
    {
        Should.NotThrow(() => _guard.EnsureChildWritable(NewWork(level, Guid.NewGuid()), false, false));
    }

    private static Work NewWork(WorkLockLevel level, Guid? analyseToken)
        => new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), LockLevel = level, AnalyseToken = analyseToken };
}
