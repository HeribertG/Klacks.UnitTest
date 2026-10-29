// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Domain.Common;

/// <summary>
/// IsoWeekday: 1..7 are valid, ISO Sunday (7) is stored as 0 and back, Monday to Saturday keep their number.
/// </summary>
[TestFixture]
public class IsoWeekdayTests
{
    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(7, true)]
    [TestCase(8, false)]
    public void IsValid(int isoWeekday, bool expected) => IsoWeekday.IsValid(isoWeekday).ShouldBe(expected);

    [TestCase(1, 1)]
    [TestCase(6, 6)]
    [TestCase(7, 0)]
    public void ToStorage_AndBack(int isoWeekday, int storageWeekday)
    {
        IsoWeekday.ToStorage(isoWeekday).ShouldBe(storageWeekday);
        IsoWeekday.FromStorage(storageWeekday).ShouldBe(isoWeekday);
    }
}
