// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the three-day refresh window the Expenses handlers (create, update, delete) answer with: the
/// schedule entries are requested for the day before to the day after the parent Work, with the caller's visible
/// group list and the Work's scenario token passed through unchanged, and only the rows of the Work's client reach
/// the response, in stub order.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Expenses;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.Application.Handlers.Works;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ExpensesEntity = Klacks.Api.Domain.Models.Schedules.Expenses;

namespace Klacks.UnitTest.Application.Handlers.Expenses;

[TestFixture]
public class ExpensesThreeDayRefreshCharacterizationTests
{
    private const string UserName = "test-user";
    private static readonly DateOnly Day = new(2027, 5, 12);
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);

    private IExpensesRepository _expensesRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDayLockService _dayLockService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    private List<Guid> _visibleGroupIds = null!;
    private Guid _analyseToken;
    private Guid _clientId;
    private Guid _otherClientId;
    private Guid _expensesId;
    private Work _work = null!;

    [SetUp]
    public void SetUp()
    {
        _expensesRepository = Substitute.For<IExpensesRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = TestGroupWriteVisibility.AllClientsVisible();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _dayLockService = Substitute.For<IDayLockService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        WorksTestHelpers.GivenUserIsAdmin(_httpContextAccessor, UserName);

        _visibleGroupIds = [Guid.NewGuid(), Guid.NewGuid()];
        _analyseToken = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();
        _expensesId = Guid.NewGuid();
        _work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            AnalyseToken = _analyseToken
        };

        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(_visibleGroupIds);
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _workRepository.GetNoTracking(_work.Id).Returns(_work);
        _expensesRepository.Put(Arg.Any<ExpensesEntity>()).Returns(call => call.Arg<ExpensesEntity>());
    }

    [Test]
    public async Task Post_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var ownRows = GivenScheduleEntries();
        GivenStoredExpenses();

        var result = await NewPostHandler().Handle(
            new PostCommand<ExpensesResource>(NewResource()), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(ownRows);
    }

    [Test]
    public async Task Put_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var ownRows = GivenScheduleEntries();
        var stored = GivenStoredExpenses();
        _expensesRepository.GetNoTracking(_expensesId).Returns(stored);

        var result = await NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource()), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(ownRows);
    }

    [Test]
    public async Task Delete_RequestsTheThreeDayWindowWithGroupsAndToken_AndReturnsOnlyTheClientRowsInStubOrder()
    {
        var ownRows = GivenScheduleEntries();
        GivenStoredExpenses();

        var result = await NewDeleteHandler().Handle(
            new DeleteCommand<ExpensesResource>(_expensesId), CancellationToken.None);

        ThenThreeDayWindowWasRequested();
        result!.ScheduleEntries.Select(e => e.Id).ShouldBe(ownRows);
    }

    private ExpensesEntity GivenStoredExpenses()
    {
        var stored = new ExpensesEntity
        {
            Id = _expensesId,
            WorkId = _work.Id,
            Work = _work,
            AnalyseToken = _analyseToken
        };
        _expensesRepository.GetWithWorkInAnyScope(_expensesId).Returns(stored);
        return stored;
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
        _scheduleEntriesService
            .GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(_ => new TestAsyncEnumerable<ScheduleCell>(cells));
        return own;
    }

    private void ThenThreeDayWindowWasRequested()
    {
        _scheduleEntriesService.Received(1).GetScheduleEntriesQuery(
            Day.AddDays(-1), Day.AddDays(1), _visibleGroupIds, _analyseToken);
    }

    private static ScheduleCell Cell(Guid clientId, Guid id)
        => new() { Id = id, ClientId = clientId, EntryId = Guid.NewGuid() };

    private ExpensesResource NewResource()
        => new() { Id = _expensesId, WorkId = _work.Id, Amount = 12.5m, Description = "Train ticket" };

    private static ParentWorkLockGuard NewGuard() => new(new WorkLockLevelService());

    private PostCommandHandler NewPostHandler()
        => new(
            _expensesRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(), _unitOfWork,
            _periodHoursService, _scheduleEntriesService, Substitute.For<IWorkNotificationService>(),
            _httpContextAccessor, Substitute.For<IScheduleChangeTracker>(), _groupContextResolver, _workRepository,
            _dayLockService, NewGuard(), Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler()
        => new(
            _expensesRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(), _unitOfWork,
            _periodHoursService, _scheduleEntriesService, Substitute.For<IWorkNotificationService>(),
            _httpContextAccessor, Substitute.For<IScheduleChangeTracker>(), _groupContextResolver, _workRepository,
            _dayLockService, NewGuard(), Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler NewDeleteHandler()
        => new(
            _expensesRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(), _unitOfWork,
            _periodHoursService, _scheduleEntriesService, Substitute.For<IWorkNotificationService>(),
            _httpContextAccessor, Substitute.For<IScheduleChangeTracker>(), _groupContextResolver,
            _dayLockService, NewGuard(), Substitute.For<ILogger<DeleteCommandHandler>>());
}
