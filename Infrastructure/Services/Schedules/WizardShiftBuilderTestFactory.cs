// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Services.Schedules;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Builds a WizardShiftBuilder whose shift schedule marks no shift day as a container child, for tests that are not
/// about containers.
/// </summary>
public static class WizardShiftBuilderTestFactory
{
    public static WizardShiftBuilder WithoutContainerChildren(DataBaseContext context)
    {
        var scheduleReader = Substitute.For<IShiftScheduleReader>();
        scheduleReader
            .GetShiftScheduleListAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<DateOnly>?>(), Arg.Any<List<Guid>?>(),
                Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ShiftDayAssignment>)new List<ShiftDayAssignment>());
        return new WizardShiftBuilder(context, scheduleReader);
    }
}
