// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the period-hours query (used by the schedule and by the get_period_hours skill) returned
/// scheduled and guaranteed hours for any client id. A client hidden by group visibility must now be dropped
/// like an unknown id, so it is never calculated and is absent from the result.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.PeriodHours;
using Klacks.Api.Application.Queries.PeriodHours;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.PeriodHours;

[TestFixture]
public class GetPeriodHoursVisibilityTests
{
    private static readonly DateOnly StartDate = new(2026, 3, 1);
    private static readonly DateOnly EndDate = new(2026, 3, 31);

    [Test]
    public async Task Handle_HiddenClient_IsDroppedLikeAnUnknownId()
    {
        var visibleClientId = Guid.NewGuid();
        var hiddenClientId = Guid.NewGuid();
        var periodHoursService = Substitute.For<IPeriodHoursService>();
        periodHoursService
            .GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>());
        var handler = new GetPeriodHoursQueryHandler(
            periodHoursService, TestGroupWriteVisibility.ClientsHidden(hiddenClientId));
        var request = new PeriodHoursRequest
        {
            ClientIds = [visibleClientId, hiddenClientId],
            StartDate = StartDate,
            EndDate = EndDate,
        };

        await handler.Handle(new GetPeriodHoursQuery(request), CancellationToken.None);

        await periodHoursService.Received(1).GetPeriodHoursAsync(
            Arg.Is<List<Guid>>(ids => ids.Count == 1 && ids[0] == visibleClientId),
            StartDate,
            EndDate,
            Arg.Any<Guid?>());
    }
}
