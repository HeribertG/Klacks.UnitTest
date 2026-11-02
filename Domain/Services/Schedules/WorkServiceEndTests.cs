// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Rule table of the service end: a service ends on its date at its end time, the next day when the span crosses
/// midnight (night shift, equal bounds as a 24 hour duty); it counts as ended from the end minute on. The database
/// filter is pinned against the in-memory check over a grid that includes every boundary.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class WorkServiceEndTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);

    private static Work Service(string start, string end, DateOnly? date = null) => new()
    {
        CurrentDate = date ?? Day,
        StartTime = TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
        EndTime = TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture)
    };

    [TestCase("08:00", "16:00", "2026-11-02 16:00", true)]
    [TestCase("08:00", "16:00", "2026-11-02 15:59", false)]
    [TestCase("08:00", "16:00", "2026-11-03 00:00", true)]
    [TestCase("22:00", "06:00", "2026-11-02 23:59", false)]
    [TestCase("22:00", "06:00", "2026-11-03 05:59", false)]
    [TestCase("22:00", "06:00", "2026-11-03 06:00", true)]
    [TestCase("07:00", "07:00", "2026-11-02 23:00", false)]
    [TestCase("07:00", "07:00", "2026-11-03 06:59", false)]
    [TestCase("07:00", "07:00", "2026-11-03 07:00", true)]
    [TestCase("22:00", "00:00", "2026-11-02 23:59", false)]
    [TestCase("22:00", "00:00", "2026-11-03 00:00", true)]
    public void HasEnded_FollowsTheEndMinute(string start, string end, string now, bool expected)
    {
        var companyNow = DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture);

        WorkServiceEnd.HasEnded(Service(start, end), companyNow).ShouldBe(expected);
    }

    [Test]
    public void HasEnded_IsFalse_ForAServiceInTheFuture()
    {
        WorkServiceEnd.HasEnded(Service("08:00", "16:00", Day.AddDays(3)), Day.ToDateTime(new TimeOnly(23, 0))).ShouldBeFalse();
    }

    [Test]
    public void EndOf_PutsANightShiftOnTheFollowingDay()
    {
        WorkServiceEnd.EndOf(Service("22:00", "06:00")).ShouldBe(new DateTime(2026, 11, 3, 6, 0, 0));
    }

    [Test]
    public void EndedBy_AgreesWithHasEnded_ForEveryBoundaryOfTheGrid()
    {
        var starts = new[] { "00:00", "06:00", "08:00", "22:00", "23:59" };
        var ends = new[] { "00:00", "06:00", "08:00", "16:00", "22:00", "23:59" };
        var nowTimes = new[] { "00:00", "05:59", "06:00", "07:59", "08:00", "15:59", "16:00", "21:59", "22:00", "23:58", "23:59" };
        var nowDays = new[] { Day.AddDays(-1), Day, Day.AddDays(1), Day.AddDays(2) };

        var checkedCombinations = 0;
        foreach (var start in starts)
        {
            foreach (var end in ends)
            {
                var work = Service(start, end);
                foreach (var nowDay in nowDays)
                {
                    foreach (var nowTime in nowTimes)
                    {
                        var now = nowDay.ToDateTime(TimeOnly.Parse(nowTime, System.Globalization.CultureInfo.InvariantCulture));
                        var cutoff = ServiceEndCutoff.From(now);

                        WorkServiceEnd.EndedBy(cutoff).Compile()(work)
                            .ShouldBe(WorkServiceEnd.HasEnded(work, now), $"{start}-{end} at {now:yyyy-MM-dd HH:mm}");
                        WorkServiceEnd.NotEndedBy(cutoff).Compile()(work)
                            .ShouldBe(!WorkServiceEnd.HasEnded(work, now), $"not-ended {start}-{end} at {now:yyyy-MM-dd HH:mm}");
                        checkedCombinations++;
                    }
                }
            }
        }

        checkedCombinations.ShouldBe(starts.Length * ends.Length * nowDays.Length * nowTimes.Length);
    }

    [Test]
    public void From_SplitsTheCompanyNowIntoDayAndTime()
    {
        var cutoff = ServiceEndCutoff.From(new DateTime(2026, 11, 2, 13, 45, 0));

        cutoff.Today.ShouldBe(Day);
        cutoff.Time.ShouldBe(new TimeOnly(13, 45));
    }
}
