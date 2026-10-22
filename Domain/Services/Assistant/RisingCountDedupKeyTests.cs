// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for RisingCountDedupKey: the per-recipient dedup key spelling (DedupKey, separator, count) and the
/// read-back of the highest delivered count, which ignores keys of other DedupKeys and keys without a readable count.
/// </summary>

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RisingCountDedupKeyTests
{
    [Test]
    public void For_AppendsTheCountToTheDedupKey()
    {
        RisingCountDedupKey.For("2026-10-22", 7).ShouldBe("2026-10-22#7");
        RisingCountDedupKey.PrefixFor("2026-10-22").ShouldBe("2026-10-22#");
    }

    [Test]
    public void HighestDeliveredCount_IsTheMaximumOfTheDaysKeys()
    {
        var keys = new[] { "2026-10-22#5", "2026-10-22#12", "2026-10-22#9" };

        RisingCountDedupKey.HighestDeliveredCount(keys, "2026-10-22").ShouldBe(12);
    }

    [Test]
    public void HighestDeliveredCount_IgnoresOtherDaysAndUnreadableKeys()
    {
        var keys = new[] { "2026-10-21#40", "2026-10-22", "2026-10-22#x", "2026-10-22#-3", "2026-10-22#3" };

        RisingCountDedupKey.HighestDeliveredCount(keys, "2026-10-22").ShouldBe(3);
    }

    [Test]
    public void HighestDeliveredCount_NothingDelivered_IsNull()
    {
        RisingCountDedupKey.HighestDeliveredCount(Array.Empty<string>(), "2026-10-22").ShouldBeNull();
    }
}
