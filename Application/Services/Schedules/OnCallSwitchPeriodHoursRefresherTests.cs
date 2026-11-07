// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// After an on-call switch the cached real-plan period hours are recomputed over the first to the last day of the type's
/// own real-plan absences, and nothing runs when the type has none. A failure after the committed switch is logged, not
/// thrown; a cancellation of the caller is passed on. The span query itself is proven against Npgsql in
/// Klacks.IntegrationTest (BreakAbsenceTypeSpanPostgresTests).
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class OnCallSwitchPeriodHoursRefresherTests
{
    private static readonly DateOnly First = new(2027, 1, 28);
    private static readonly DateOnly Last = new(2027, 6, 3);

    private IBreakRepository _breakRepository = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private ILogger<OnCallSwitchPeriodHoursRefresher> _logger = null!;
    private OnCallSwitchPeriodHoursRefresher _refresher = null!;
    private Guid _absenceId;

    [SetUp]
    public void SetUp()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _logger = Substitute.For<ILogger<OnCallSwitchPeriodHoursRefresher>>();
        _refresher = new OnCallSwitchPeriodHoursRefresher(_breakRepository, _periodHoursService, _logger);
        _absenceId = Guid.NewGuid();
    }

    [Test]
    public async Task RefreshesTheCachedRealPlanRows_OverTheSpanOfTheTypesAbsences()
    {
        _breakRepository.GetRealPlanSpanOfAbsenceTypeAsync(_absenceId, CancellationToken.None)
            .Returns(((DateOnly First, DateOnly Last)?)(First, Last));

        await _refresher.RefreshAsync(_absenceId, CancellationToken.None);

        await _periodHoursService.Received(1).RefreshCachedPeriodHoursAsync(First, Last, null);
    }

    [Test]
    public async Task DoesNothing_WhenTheTypeHasNoRealPlanAbsences()
    {
        _breakRepository.GetRealPlanSpanOfAbsenceTypeAsync(_absenceId, CancellationToken.None)
            .Returns(((DateOnly First, DateOnly Last)?)null);

        await _refresher.RefreshAsync(_absenceId, CancellationToken.None);

        await _periodHoursService.DidNotReceiveWithAnyArgs().RefreshCachedPeriodHoursAsync(default, default, default);
    }

    [Test]
    public async Task AFailingRefresh_IsLogged_AndDoesNotThrow()
    {
        _breakRepository.GetRealPlanSpanOfAbsenceTypeAsync(_absenceId, CancellationToken.None)
            .Returns(((DateOnly First, DateOnly Last)?)(First, Last));
        _periodHoursService.RefreshCachedPeriodHoursAsync(First, Last, null)
            .Returns<Task>(_ => throw new InvalidOperationException("injected"));

        await Should.NotThrowAsync(() => _refresher.RefreshAsync(_absenceId, CancellationToken.None));

        _logger.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && call.GetArguments()[0] is LogLevel.Error)
            .ShouldBe(1);
    }

    [Test]
    public async Task ACancelledCaller_GetsTheCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _breakRepository.GetRealPlanSpanOfAbsenceTypeAsync(_absenceId, cancellation.Token)
            .Returns<Task<(DateOnly First, DateOnly Last)?>>(_ => throw new OperationCanceledException(cancellation.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => _refresher.RefreshAsync(_absenceId, cancellation.Token));
    }
}
