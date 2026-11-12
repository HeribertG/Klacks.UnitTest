// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// The rule a swap-chain probe admission was refused by, as far as the public building blocks of the assignment
/// filter can name it. The first five are checked directly; the others are found by switching off one rule at a
/// time on a copy of the employee, so a placement that breaks two of them at once is reported as
/// <see cref="SeveralOrOther"/>.
/// </summary>
public enum SwapChainProbeRule
{
    /// <summary>The shift overlaps another shift or an existing or locked work of the employee.</summary>
    Collision,

    /// <summary>The calendar week would keep fewer rest days than the employee's minimum.</summary>
    WeeklyRestDays,

    /// <summary>Qualification, contract day or weekday, shift-work flag, absence, keyword, blacklist or restricted window.</summary>
    DayVeto,

    /// <summary>The calendar week would exceed the weekly hour cap.</summary>
    WeeklyHours,

    /// <summary>Less than the minimum pause (11 h) to another shift.</summary>
    MinPause,

    /// <summary>Less than the package rest (MinRestDays as hours, 48 h) between two packages.</summary>
    PackageRest,

    /// <summary>The block would exceed the MaxWorkDays ideal.</summary>
    BlockIdeal,

    /// <summary>The block would exceed the MaxConsecutiveDays cap; a night counts on every calendar day it touches.</summary>
    ConsecutiveDaysCap,

    /// <summary>The day would exceed the employee's daily hour cap.</summary>
    DailyHours,

    /// <summary>Several rules at once, or a rule without its own switch (keyword wish, MaximumHours, contract-day cap).</summary>
    SeveralOrOther,
}
