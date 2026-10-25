// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the shared monthly-table analysis: missing months of a partly filled table (an empty table is not partial),
/// and month values beyond the named deviation threshold from workdays x daily hours.
/// </summary>

using Klacks.Api.Application.Services.Schedules;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class MonthlyTableAnalyzerTests
{
    private static readonly IReadOnlyDictionary<int, int> Workdays = Enumerable.Range(1, 12).ToDictionary(month => month, _ => 22);

    [Test]
    public void MissingMonths_PartialTable_ListsTheGaps_EmptyTable_IsNotPartial()
    {
        var partial = new Dictionary<int, decimal> { [1] = 176m, [12] = 160m };

        MonthlyTableAnalyzer.MissingMonths(partial).ShouldBe([2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        MonthlyTableAnalyzer.IsPartiallyFilled(partial).ShouldBeTrue();
        MonthlyTableAnalyzer.IsPartiallyFilled(new Dictionary<int, decimal>()).ShouldBeFalse();
        MonthlyTableAnalyzer.MissingMonths(new Dictionary<int, decimal>()).ShouldBeEmpty();
    }

    [Test]
    public void DeviatingMonths_OnlyBeyondTheThreshold()
    {
        var rows = new Dictionary<int, decimal> { [1] = 176m, [2] = 160m, [3] = 120m };

        var deviating = MonthlyTableAnalyzer.DeviatingMonths(rows, Workdays, 8m);

        deviating.ShouldBe(["3: 120 h vs 22 workdays x 8 h = 176 h (31.82 %)"]);
    }

    [Test]
    public void DeviatingMonths_WithoutDailyHours_ReportsNothing()
    {
        MonthlyTableAnalyzer.DeviatingMonths(new Dictionary<int, decimal> { [1] = 10m }, Workdays, 0m).ShouldBeEmpty();
    }
}
