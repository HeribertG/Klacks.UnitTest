// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderWorkHours: only hour-bearing changes (corrections, travel) add to the hours of a work
/// entry in the order exports; replacements and briefing/debriefing add nothing.
/// </summary>
using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class OrderWorkHoursTests
{
    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public void BillableHours_ReplacementChange_DoesNotAddHours(WorkChangeType type)
    {
        var work = BuildWork(type);

        OrderWorkHours.BillableHours(work).ShouldBe(8.5m);
        OrderWorkHours.WorkHours(work).ShouldBe(8m);
    }

    [TestCase(WorkChangeType.Briefing)]
    [TestCase(WorkChangeType.Debriefing)]
    public void BillableHours_BriefingChange_DoesNotAddHours(WorkChangeType type)
    {
        var work = BuildWork(type);

        OrderWorkHours.BillableHours(work).ShouldBe(8.5m);
        OrderWorkHours.WorkHours(work).ShouldBe(8m);
    }

    [TestCase(WorkChangeType.CorrectionStart)]
    [TestCase(WorkChangeType.CorrectionEnd)]
    [TestCase(WorkChangeType.TravelStart)]
    [TestCase(WorkChangeType.TravelEnd)]
    [TestCase(WorkChangeType.TravelWithin)]
    public void BillableHours_HourBearingChange_AddsHoursAndSurcharges(WorkChangeType type)
    {
        var work = BuildWork(type);

        OrderWorkHours.BillableHours(work).ShouldBe(11m);
        OrderWorkHours.WorkHours(work).ShouldBe(10m);
    }

    [Test]
    public void BillableHours_MixedChanges_AddsOnlyHourBearing()
    {
        var work = BuildWork(WorkChangeType.CorrectionEnd);
        work.Changes.Add(new WorkChangeExportEntry { Type = WorkChangeType.ReplacementWithin, ChangeTime = 3m, Surcharges = 1m });
        work.Changes.Add(new WorkChangeExportEntry { Type = WorkChangeType.Briefing, ChangeTime = 0.5m });

        OrderWorkHours.BillableHours(work).ShouldBe(11m);
    }

    private static WorkExportEntry BuildWork(WorkChangeType changeType)
    {
        return new WorkExportEntry
        {
            WorkId = Guid.NewGuid(),
            WorkDate = new DateOnly(2026, 1, 10),
            WorkTime = 8m,
            Surcharges = 0.5m,
            Changes =
            [
                new WorkChangeExportEntry { Type = changeType, ChangeTime = 2m, Surcharges = 0.5m },
            ],
        };
    }
}
