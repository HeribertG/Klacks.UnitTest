// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the three-day refresh window the Break handlers (create, update, delete) answer with: the
/// schedule entries are requested for the day before to the day after the break, with the caller's visible group list
/// and the break's scenario token passed through unchanged, and only the rows of the break's client reach the
/// response, in stub order.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Breaks;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Application.Handlers.Breaks;

[TestFixture]
public class BreakThreeDayRefreshCharacterizationTests
{
    private static readonly DateOnly Day = new(2027, 5, 12);
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);

    private IBreakRepository _breakRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IBreakMacroService _breakMacroService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IScheduleCompletionService _completionService = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IDayLockService _dayLockService = null!;

    private List<Guid> _visibleGroupIds = null!;
    private Guid _analyseToken;
    private Guid _clientId;
    private Guid _otherClientId;
    private Guid _breakId;
    private Guid _absenceId;

    [SetUp]
    public void SetUp()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _clientVisibilityGuard = TestGroupWriteVisibility.AllClientsVisible();
        _breakMacroService = Substitute.For<IBreakMacroService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _dayLockService = Substitute.For<IDayLockService>();

        _visibleGroupIds = [Guid.NewGuid(), Guid.NewGuid()];
        _analyseToken = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();
        _breakId = Guid.NewGuid();
        _absenceId = Guid.NewGuid();

        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(_visibleGroupIds);
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _completionService
            .SaveAndTrackAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new PeriodHoursResource { Hours = 8 });
        _breakRepository.Put(Arg.Any<Break>()).Returns(call => call.Arg<Break>());
    }

    [Test]
    public async Task Post_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var ownRows = GivenScheduleEntries();
        var resource = new BreakResource
        {
            Id = _breakId, ClientId = _clientId, AbsenceId = _absenceId, CurrentDate = Day, AnalyseToken = _analyseToken
        };

        var result = await NewPostHandler().Handle(new PostCommand<BreakResource>(resource), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(ownRows);
    }

    [Test]
    public async Task Put_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var ownRows = GivenScheduleEntries();
        _breakRepository.GetNoTracking(_breakId).Returns(NewStoredBreak());
        var resource = new BreakResource { Id = _breakId, ClientId = _clientId, AbsenceId = _absenceId, CurrentDate = Day };

        var result = await NewPutHandler().Handle(new PutCommand<BreakResource>(resource), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(ownRows);
    }

    [Test]
    public async Task Delete_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var ownRows = GivenScheduleEntries();
        _breakRepository.Get(_breakId).Returns(NewStoredBreak());

        var result = await NewDeleteHandler().Handle(new DeleteBreakCommand(_breakId, Day, Day), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(ownRows);
    }

    private Guid[] GivenScheduleEntries()
    {
        var own = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var foreign = new[] { Guid.NewGuid(), Guid.NewGuid() };
        ScheduleCell[] cells =
        [
            Cell(_clientId, own[0]),
            Cell(_otherClientId, foreign[0]),
            Cell(_clientId, own[1]),
            Cell(_otherClientId, foreign[1]),
        ];
        ScheduleEntriesStub.Serve(_scheduleEntriesService, cells);
        return own;
    }

    private void ThenThreeDayWindowWasRequested(params Guid[] clientIds)
    {
        Guid[] expectedClients = clientIds.Length == 0 ? [_clientId] : clientIds;
        _scheduleEntriesService.Received(1).GetScheduleEntriesAsync(
            Arg.Is<ScheduleEntriesCriteria>(c =>
                c.From == Day.AddDays(-1)
                && c.Until == Day.AddDays(1)
                && ReferenceEquals(c.VisibleGroupIds, _visibleGroupIds)
                && c.AnalyseToken == _analyseToken
                && c.ClientIds != null
                && c.ClientIds.SequenceEqual(expectedClients)
                && c.EntryType == null
                && !c.OrderByStartTime),
            Arg.Any<CancellationToken>());
    }

    private static ScheduleCell Cell(Guid clientId, Guid id)
        => new() { Id = id, ClientId = clientId, EntryId = Guid.NewGuid() };

    private Break NewStoredBreak()
        => new()
        {
            Id = _breakId,
            ClientId = _clientId,
            AbsenceId = _absenceId,
            CurrentDate = Day,
            AnalyseToken = _analyseToken
        };

    private PostCommandHandler NewPostHandler()
        => new(
            _breakRepository, _clientVisibilityGuard, _breakMacroService, new ScheduleMapper(), _periodHoursService,
            _scheduleEntriesService, _notificationService, _completionService, Substitute.For<IHttpContextAccessor>(),
            _groupContextResolver, _dayLockService, Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler()
        => new(
            _breakRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), _breakMacroService,
            new ScheduleMapper(), _periodHoursService, _scheduleEntriesService, _notificationService,
            _completionService, Substitute.For<IHttpContextAccessor>(), _groupContextResolver, _dayLockService,
            Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler NewDeleteHandler()
        => new(
            _breakRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            _periodHoursService, _scheduleEntriesService, _notificationService, _completionService,
            Substitute.For<IHttpContextAccessor>(), _groupContextResolver, _dayLockService,
            Substitute.For<ILogger<DeleteCommandHandler>>());
}
