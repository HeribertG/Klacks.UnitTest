// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

/// <summary>
/// The validity period filter of the shift list: a shift matches when FromDate..UntilDate (UntilDate empty = open) overlaps
/// the chosen period with inclusive bounds; an empty lower bound reaches back without limit, an empty upper bound forward
/// without limit, both empty = no period filter.
/// </summary>
[TestFixture]
public class ShiftValidityOverlapTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jan31 = new(2026, 1, 31);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);
    private static readonly DateOnly Jun1 = new(2026, 6, 1);

    [Test]
    public void BothBoundsEmpty_IsNoPeriodFilter()
    {
        ShiftValidityOverlap.HasPeriod(null, null).ShouldBeFalse();
        ShiftValidityOverlap.HasPeriod(Mar1, null).ShouldBeTrue();
        ShiftValidityOverlap.HasPeriod(null, Mar31).ShouldBeTrue();
        Matches(null, null, NewShift(Jan1, Jan31)).ShouldBeTrue();
        Matches(null, null, NewShift(Jan1, null)).ShouldBeTrue();
    }

    [Test]
    public void ClosedPeriod_MatchesShiftsThatOverlapIt()
    {
        Matches(Mar1, Mar31, NewShift(Jan1, Jan31)).ShouldBeFalse();
        Matches(Mar1, Mar31, NewShift(Jan1, Mar1)).ShouldBeTrue();
        Matches(Mar1, Mar31, NewShift(Mar31, Jun1)).ShouldBeTrue();
        Matches(Mar1, Mar31, NewShift(Jun1, null)).ShouldBeFalse();
        Matches(Mar1, Mar31, NewShift(Jan1, Jun1)).ShouldBeTrue();
        Matches(Mar1, Mar31, NewShift(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 12))).ShouldBeTrue();
    }

    [Test]
    public void OpenEndedShift_OverlapsEveryPeriodStartingAfterItsFromDate()
    {
        Matches(Mar1, Mar31, NewShift(Jan1, null)).ShouldBeTrue();
        Matches(Jun1, null, NewShift(Jan1, null)).ShouldBeTrue();
        Matches(Jan1, Jan31, NewShift(Mar1, null)).ShouldBeFalse();
    }

    [Test]
    public void EmptyFrom_ReachesBackWithoutLimit()
    {
        Matches(null, Mar31, NewShift(Jan1, Jan31)).ShouldBeTrue();
        Matches(null, Jan1, NewShift(Jan1, null)).ShouldBeTrue();
        Matches(null, Jan31, NewShift(Mar1, Jun1)).ShouldBeFalse();
    }

    [Test]
    public void EmptyUntil_IsOpenTowardsTheFuture()
    {
        Matches(Mar1, null, NewShift(Jan1, Jan31)).ShouldBeFalse();
        Matches(Mar1, null, NewShift(Jan1, Mar1)).ShouldBeTrue();
        Matches(Mar1, null, NewShift(Jun1, Jun1)).ShouldBeTrue();
    }

    private static bool Matches(DateOnly? from, DateOnly? until, Shift shift)
        => ShiftValidityOverlap.Overlaps(from, until).Compile()(shift);

    private static Shift NewShift(DateOnly fromDate, DateOnly? untilDate) => new()
    {
        Id = Guid.NewGuid(),
        FromDate = fromDate,
        UntilDate = untilDate
    };
}
