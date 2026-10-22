// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using NSubstitute;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Answers a substituted IScheduleEntriesService from an in-memory row list and applies the criteria's client,
/// entry-type and start-time narrowing the way the database does (null client set = every client, empty = none),
/// so handler tests keep proving that only the requested rows reach the response. The SQL itself is pinned by
/// the integration test ScheduleEntriesReadShapesTests.
/// </summary>
internal static class ScheduleEntriesStub
{
    public static void Serve(IScheduleEntriesService service, IEnumerable<ScheduleCell> rows)
    {
        var all = rows.ToList();
        service.GetScheduleEntriesAsync(Arg.Any<ScheduleEntriesCriteria>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Apply(all, call.Arg<ScheduleEntriesCriteria>())));
    }

    public static void Serve(
        IScheduleEntriesService service,
        Func<ScheduleEntriesCriteria, bool> when,
        IEnumerable<ScheduleCell> rows)
    {
        var all = rows.ToList();
        service.GetScheduleEntriesAsync(Arg.Is<ScheduleEntriesCriteria>(c => when(c)), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Apply(all, call.Arg<ScheduleEntriesCriteria>())));
    }

    public static void ServeNothing(IScheduleEntriesService service) => Serve(service, []);

    public static IReadOnlyList<ScheduleCell> Apply(IReadOnlyList<ScheduleCell> rows, ScheduleEntriesCriteria criteria)
    {
        IEnumerable<ScheduleCell> result = rows;
        if (criteria.ClientIds is not null)
        {
            result = result.Where(r => criteria.ClientIds.Contains(r.ClientId));
        }

        if (criteria.EntryType is { } entryType)
        {
            result = result.Where(r => r.EntryType == (int)entryType);
        }

        if (criteria.OrderByStartTime)
        {
            result = result.OrderBy(r => r.StartTime);
        }

        return result.ToList();
    }
}
