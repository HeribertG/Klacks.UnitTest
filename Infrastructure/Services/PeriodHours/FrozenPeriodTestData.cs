// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Seeds the rows that freeze a pay period of a person: payroll export items for several ranges and a sealed
/// (LockLevel Closed) work inside the period.
/// </summary>

using Klacks.Api.Domain.Models.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

internal static class FrozenPeriodTestData
{
    public static readonly (DateOnly StartDate, DateOnly EndDate) January = (new(2099, 1, 1), new(2099, 1, 31));
    public static readonly (DateOnly StartDate, DateOnly EndDate) February = (new(2099, 2, 1), new(2099, 2, 28));
    public static readonly (DateOnly StartDate, DateOnly EndDate) March = (new(2099, 3, 1), new(2099, 3, 31));
    public static readonly (DateOnly StartDate, DateOnly EndDate) April = (new(2099, 4, 1), new(2099, 4, 30));

    public static void AddExports(DataBaseContext context, Guid clientId, params (DateOnly StartDate, DateOnly EndDate)[] ranges)
    {
        foreach (var (startDate, endDate) in ranges)
        {
            context.ExportLogItem.Add(new ExportLogItem
            {
                Id = Guid.NewGuid(),
                ExportLogId = Guid.NewGuid(),
                ClientId = clientId,
                StartDate = startDate,
                EndDate = endDate,
                Format = "test",
                Revision = 1
            });
        }
    }

    public static Work AddWork(
        DataBaseContext context,
        Guid clientId,
        DateOnly date,
        WorkLockLevel lockLevel,
        decimal workTime = 8m,
        Guid? analyseToken = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            WorkTime = workTime,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            LockLevel = lockLevel,
            AnalyseToken = analyseToken
        };
        context.Work.Add(work);
        return work;
    }

    /// <summary>
    /// Freezes January for the client: sealed work in January plus exports of January, February and March, so the
    /// window (latest two export ranges) starts on 1 February.
    /// </summary>
    public static void FreezeJanuary(DataBaseContext context, Guid clientId, decimal workTime = 8m)
    {
        AddWork(context, clientId, new DateOnly(2099, 1, 10), WorkLockLevel.Closed, workTime);
        AddExports(context, clientId, January, February, March);
    }
}
