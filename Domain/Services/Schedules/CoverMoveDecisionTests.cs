// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The rule that separates a moved work from a replacement: only a whole, untouched work moves.
/// </summary>

using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class CoverMoveDecisionTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly TimeOnly Start = new(22, 0);
    private static readonly TimeOnly End = new(6, 0);

    private static CoverMoveFacts Facts(
        Guid? clientId = null,
        Guid? parentWorkId = null,
        WorkLockLevel lockLevel = WorkLockLevel.None,
        bool hasReplacement = false,
        bool hasLockedChild = false)
        => new(
            Guid.NewGuid(), clientId ?? Owner, parentWorkId, Start, End, lockLevel,
            hasReplacement, hasLockedChild);

    [Test]
    public void AWholeUntouchedWork_OverMidnight_IsCovered()
        => CoverMoveDecision.CoversWholeWork(Facts(), Owner, Start, End).ShouldBeTrue();

    [Test]
    public void APartOfTheWork_IsNotCovered()
    {
        CoverMoveDecision.CoversWholeWork(Facts(), Owner, new TimeOnly(23, 0), End).ShouldBeFalse();
        CoverMoveDecision.CoversWholeWork(Facts(), Owner, Start, new TimeOnly(5, 0)).ShouldBeFalse();
    }

    [Test]
    public void AWorkOfSomeoneElse_IsNotCovered()
        => CoverMoveDecision.CoversWholeWork(Facts(clientId: Guid.NewGuid()), Owner, Start, End).ShouldBeFalse();

    [Test]
    public void AContainerChild_IsNotCovered()
        => CoverMoveDecision.CoversWholeWork(Facts(parentWorkId: Guid.NewGuid()), Owner, Start, End).ShouldBeFalse();

    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public void ASealedWork_IsNotCovered(WorkLockLevel lockLevel)
        => CoverMoveDecision.CoversWholeWork(Facts(lockLevel: lockLevel), Owner, Start, End).ShouldBeFalse();

    [Test]
    public void AWorkWithALiveReplacement_IsNotCovered()
        => CoverMoveDecision.CoversWholeWork(Facts(hasReplacement: true), Owner, Start, End).ShouldBeFalse();

    [Test]
    public void AWorkWithASealedChild_IsNotCovered()
        => CoverMoveDecision.CoversWholeWork(Facts(hasLockedChild: true), Owner, Start, End).ShouldBeFalse();
}
