// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PeriodUnsealCounts: how the recorded pre-seal levels of reopened entries are classified, and that
/// entries without a record are reported separately instead of being counted as restored.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Domain.Models.Schedules;

[TestFixture]
public class PeriodUnsealCountsTests
{
    [Test]
    public void FromPreSealLevels_SplitsByRecordedLevel()
    {
        var counts = PeriodUnsealCounts.FromPreSealLevels(new (WorkLockLevel?, int)[]
        {
            (WorkLockLevel.Confirmed, 3),
            (WorkLockLevel.Approved, 2),
            (WorkLockLevel.None, 5),
            (null, 4)
        });

        counts.ShouldBe(new PeriodUnsealCounts(3, 2, 5, 4));
        counts.Total.ShouldBe(14);
    }

    [Test]
    public void FromPreSealLevels_CountsEntriesWithoutRecord_NeverAsRestored()
    {
        var counts = PeriodUnsealCounts.FromPreSealLevels(new (WorkLockLevel?, int)[] { (null, 7) });

        counts.WithoutRecordedLevel.ShouldBe(7);
        counts.RestoredConfirmed.ShouldBe(0);
        counts.RestoredApproved.ShouldBe(0);
        counts.RestoredNone.ShouldBe(0);
    }

    [Test]
    public void FromPreSealLevels_WithNoEntries_IsEmpty()
    {
        PeriodUnsealCounts.FromPreSealLevels(Array.Empty<(WorkLockLevel?, int)>()).ShouldBe(PeriodUnsealCounts.Empty);
    }

    [Test]
    public void Addition_SumsEveryBucket()
    {
        var sum = new PeriodUnsealCounts(1, 2, 3, 4) + new PeriodUnsealCounts(10, 20, 30, 40);

        sum.ShouldBe(new PeriodUnsealCounts(11, 22, 33, 44));
        sum.Total.ShouldBe(110);
    }
}
