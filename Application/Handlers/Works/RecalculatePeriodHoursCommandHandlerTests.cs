// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// RecalculatePeriodHoursCommandHandler stores the period hours of all clients once per real pay period its window
/// touches, never over the requested window itself, while the breaks are reprocessed and the reload is announced over
/// the requested window.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class RecalculatePeriodHoursCommandHandlerTests
{
    private static readonly DateOnly WindowStart = new(2026, 6, 10);
    private static readonly DateOnly WindowEnd = new(2026, 7, 5);
    private static readonly (DateOnly StartDate, DateOnly EndDate) JunePeriod = (new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
    private static readonly (DateOnly StartDate, DateOnly EndDate) JulyPeriod = (new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IBreakMacroService _breakMacroService = null!;

    [SetUp]
    public void SetUp()
    {
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _breakMacroService = Substitute.For<IBreakMacroService>();
        _periodHoursService.GetPeriodsInRangeAsync(WindowStart, WindowEnd)
            .Returns(new List<(DateOnly StartDate, DateOnly EndDate)> { JunePeriod, JulyPeriod });
    }

    [Test]
    public async Task WindowOverTwoPayPeriods_RecalculatesAllClientsOncePerPayPeriod_NeverOverTheWindow()
    {
        var groupId = Guid.NewGuid();

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, groupId), CancellationToken.None);

        result.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _breakMacroService.ReprocessAllBreaksAsync(WindowStart, WindowEnd);
            _periodHoursService.RecalculateAllClientsAsync(JunePeriod.StartDate, JunePeriod.EndDate, groupId);
            _periodHoursService.RecalculateAllClientsAsync(JulyPeriod.StartDate, JulyPeriod.EndDate, groupId);
            _notificationService.NotifyPeriodHoursRecalculated(WindowStart, WindowEnd, null);
        });
        await _periodHoursService.DidNotReceive().RecalculateAllClientsAsync(WindowStart, WindowEnd, groupId);
    }

    private RecalculatePeriodHoursCommandHandler NewHandler() => new(
        _periodHoursService,
        _notificationService,
        _breakMacroService,
        Substitute.For<ILogger<RecalculatePeriodHoursCommandHandler>>());
}
