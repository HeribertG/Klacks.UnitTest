// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Bulk add and bulk delete of breaks persist the period hours after the commit, once per client, scenario token and real
/// pay period, the state single break writes leave: readers prefer a cached row over the live sum and the payroll export
/// reads only the cached rows. Before 2026-11-07 both handlers only answered live hours and left the cached rows stale.
/// </summary>

using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Breaks;
using Klacks.Api.Application.Interfaces.Schedules;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Breaks;

[TestFixture]
public class BulkBreaksPeriodHoursCacheTests
{
    private static readonly DateOnly Day = new(2027, 4, 12);
    private static readonly DateOnly PeriodStart = new(2027, 4, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 4, 30);
    private static readonly DateOnly NextPeriodDay = new(2027, 5, 3);
    private static readonly DateOnly NextPeriodStart = new(2027, 5, 1);
    private static readonly DateOnly NextPeriodEnd = new(2027, 5, 31);

    private IBreakRepository _breakRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleCompletionService _completionService = null!;
    private Guid _clientId;
    private Guid _otherClientId;
    private Guid _scenarioToken;

    [SetUp]
    public void SetUp()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Break>>(), Arg.Any<Func<Break, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Break>>(0).ToList());
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _periodHoursService.GetPeriodBoundariesAsync(Day.AddDays(1)).Returns((PeriodStart, PeriodEnd));
        _periodHoursService.GetPeriodBoundariesAsync(NextPeriodDay).Returns((NextPeriodStart, NextPeriodEnd));
        _completionService = Substitute.For<IScheduleCompletionService>();

        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();
        _scenarioToken = Guid.NewGuid();
    }

    [Test]
    public async Task BulkAdd_StoresThePeriodHours_AfterTheCommit_OncePerClientTokenAndPayPeriod()
    {
        var request = new BulkAddBreaksRequest
        {
            PeriodStart = PeriodStart,
            PeriodEnd = NextPeriodEnd,
            Breaks =
            [
                NewItem(_clientId, Day, null),
                NewItem(_clientId, Day.AddDays(1), null),
                NewItem(_clientId, NextPeriodDay, null),
                NewItem(_otherClientId, Day, _scenarioToken)
            ]
        };

        await NewAddHandler().Handle(new BulkAddBreaksCommand(request, RemoveTakenWorks: false), CancellationToken.None);

        Received.InOrder(() =>
        {
            _completionService.SaveBulkAndTrackAsync(Arg.Any<List<(Guid, DateOnly, Guid?)>>());
            _periodHoursService.RecalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, null);
            _periodHoursService.RecalculatePeriodHoursAsync(_clientId, NextPeriodStart, NextPeriodEnd, null);
            _periodHoursService.RecalculatePeriodHoursAsync(_otherClientId, PeriodStart, PeriodEnd, _scenarioToken);
        });
        RecalculationCount().ShouldBe(3);
    }

    [Test]
    public async Task BulkDelete_StoresThePeriodHours_AfterTheCommit_OncePerClientTokenAndPayPeriod()
    {
        var breaks = new List<Break>
        {
            NewBreak(_clientId, Day, null),
            NewBreak(_clientId, Day.AddDays(1), null),
            NewBreak(_otherClientId, NextPeriodDay, _scenarioToken)
        };
        _breakRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(breaks);

        await NewDeleteHandler().Handle(
            new BulkDeleteBreaksCommand(new BulkDeleteBreaksRequest
            {
                BreakIds = breaks.Select(b => b.Id).ToList(),
                PeriodStart = PeriodStart,
                PeriodEnd = NextPeriodEnd
            }),
            CancellationToken.None);

        Received.InOrder(() =>
        {
            _completionService.SaveBulkAndTrackAsync(Arg.Any<List<(Guid, DateOnly, Guid?)>>());
            _periodHoursService.RecalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, null);
            _periodHoursService.RecalculatePeriodHoursAsync(_otherClientId, NextPeriodStart, NextPeriodEnd, _scenarioToken);
        });
        RecalculationCount().ShouldBe(2);
    }

    private int RecalculationCount()
        => _periodHoursService.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IPeriodHoursService.RecalculatePeriodHoursAsync));

    private static BulkBreakItem NewItem(Guid clientId, DateOnly day, Guid? analyseToken) => new()
    {
        ClientId = clientId,
        AbsenceId = Guid.NewGuid(),
        CurrentDate = day,
        AnalyseToken = analyseToken
    };

    private static Break NewBreak(Guid clientId, DateOnly day, Guid? analyseToken) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        AbsenceId = Guid.NewGuid(),
        CurrentDate = day,
        AnalyseToken = analyseToken
    };

    private BulkAddBreaksCommandHandler NewAddHandler() => new(
        _breakRepository, _clientVisibilityGuard, Substitute.For<IBreakMacroService>(),
        Substitute.For<IAbsenceWorkRemovalService>(), _periodHoursService, _completionService,
        Substitute.For<IDayLockService>(), Substitute.For<ILogger<BulkAddBreaksCommandHandler>>());

    private BulkDeleteBreaksCommandHandler NewDeleteHandler() => new(
        _breakRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), _periodHoursService, _completionService,
        Substitute.For<IDayLockService>(), Substitute.For<ILogger<BulkDeleteBreaksCommandHandler>>());
}
