using System.Collections;
using System.Linq.Expressions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using WorkPostHandler = Klacks.Api.Application.Handlers.Works.PostCommandHandler;
using WorkPutHandler = Klacks.Api.Application.Handlers.Works.PutCommandHandler;
using WorkDeleteHandler = Klacks.Api.Application.Handlers.Works.DeleteCommandHandler;
using BreakPostHandler = Klacks.Api.Application.Handlers.Breaks.PostCommandHandler;
using BreakPutHandler = Klacks.Api.Application.Handlers.Breaks.PutCommandHandler;
using BreakDeleteHandler = Klacks.Api.Application.Handlers.Breaks.DeleteCommandHandler;
using ExpensesPostHandler = Klacks.Api.Application.Handlers.Expenses.PostCommandHandler;
using ExpensesPutHandler = Klacks.Api.Application.Handlers.Expenses.PutCommandHandler;
using ExpensesDeleteHandler = Klacks.Api.Application.Handlers.Expenses.DeleteCommandHandler;
using WorkChangePostHandler = Klacks.Api.Application.Handlers.WorkChanges.PostCommandHandler;
using WorkChangePutHandler = Klacks.Api.Application.Handlers.WorkChanges.PutCommandHandler;
using WorkChangeDeleteHandler = Klacks.Api.Application.Handlers.WorkChanges.DeleteCommandHandler;
using BulkAddWorksCommandHandler = Klacks.Api.Application.Handlers.Works.BulkAddWorksCommandHandler;
using BulkDeleteWorksCommandHandler = Klacks.Api.Application.Handlers.Works.BulkDeleteWorksCommandHandler;
using BulkAddBreaksCommandHandler = Klacks.Api.Application.Handlers.Breaks.BulkAddBreaksCommandHandler;
using BulkDeleteBreaksCommandHandler = Klacks.Api.Application.Handlers.Breaks.BulkDeleteBreaksCommandHandler;

namespace Klacks.UnitTest.Handlers.ScheduleChanges;

internal class TestAsyncEnumerable<T> : IQueryable<T>, IAsyncEnumerable<T>
{
    private readonly EnumerableQuery<T> _inner;

    public TestAsyncEnumerable(IEnumerable<T> enumerable)
    {
        _inner = new EnumerableQuery<T>(enumerable);
    }

    public TestAsyncEnumerable(Expression expression)
    {
        _inner = new EnumerableQuery<T>(expression);
    }

    public Type ElementType => ((IQueryable)_inner).ElementType;
    public Expression Expression => ((IQueryable)_inner).Expression;
    public IQueryProvider Provider => new TestAsyncQueryProvider(((IQueryable)_inner).Provider);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_inner).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new TestAsyncEnumerator<T>(((IEnumerable<T>)_inner).GetEnumerator());
}

internal class TestAsyncQueryProvider : IQueryProvider
{
    private readonly IQueryProvider _inner;

    public TestAsyncQueryProvider(IQueryProvider inner) => _inner = inner;

    public IQueryable CreateQuery(Expression expression)
        => throw new NotImplementedException();

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new TestAsyncEnumerable<TElement>(expression);

    public object? Execute(Expression expression) => _inner.Execute(expression);
    public TResult Execute<TResult>(Expression expression) => _inner.Execute<TResult>(expression);
}

internal class TestAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IEnumerator<T> _inner;

    public TestAsyncEnumerator(IEnumerator<T> inner) => _inner = inner;

    public T Current => _inner.Current;

    public ValueTask<bool> MoveNextAsync() => new(_inner.MoveNext());

    public ValueTask DisposeAsync()
    {
        _inner.Dispose();
        return ValueTask.CompletedTask;
    }
}

[TestFixture]
public class ScheduleChangeTrackingTests
{
    private static readonly Guid TestClientId = Guid.NewGuid();
    private static readonly Guid TestReplaceClientId = Guid.NewGuid();
    private static readonly Guid TestShiftId = Guid.NewGuid();
    private static readonly Guid TestAbsenceId = Guid.NewGuid();
    private static readonly DateOnly TestDate = new(2026, 2, 20);
    private static readonly DateOnly PeriodStart = new(2026, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 2, 28);

    private IScheduleChangeTracker _scheduleChangeTracker = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IScheduleCompletionService _completionService = null!;
    private ScheduleMapper _scheduleMapper = null!;

    [SetUp]
    public void SetUp()
    {
        _scheduleChangeTracker = Substitute.For<IScheduleChangeTracker>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.CompleteAsync().Returns(Task.FromResult(1));

        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _httpContextAccessor.HttpContext.Returns(new DefaultHttpContext());

        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns((PeriodStart, PeriodEnd));
        _periodHoursService.RecalculateAndNotifyAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>())
            .Returns(new PeriodHoursResource());
        _periodHoursService.CalculatePeriodHoursAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new PeriodHoursResource());

        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _scheduleEntriesService
            .GetScheduleEntriesQuery(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(Enumerable.Empty<ScheduleCell>()));

        _notificationService = Substitute.For<IWorkNotificationService>();

        _completionService = Substitute.For<IScheduleCompletionService>();
        _completionService.SaveAndTrackAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new PeriodHoursResource());
        _completionService.SaveAndTrackMoveAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<DateOnly?>())
            .Returns(new PeriodHoursResource());
        _completionService.SaveBulkAndTrackAsync(
                Arg.Any<List<(Guid, DateOnly)>>())
            .Returns(Task.CompletedTask);
        _completionService.SaveAndTrackWithReplaceClientAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>())
            .Returns(Task.CompletedTask);

        _scheduleMapper = new ScheduleMapper();
    }

    private static Work CreateTestWork(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ClientId = TestClientId,
        ShiftId = TestShiftId,
        CurrentDate = TestDate,
        WorkTime = 8,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
    };

    private static Api.Domain.Models.Schedules.Break CreateTestBreak(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ClientId = TestClientId,
        AbsenceId = TestAbsenceId,
        CurrentDate = TestDate,
        WorkTime = 1,
        StartTime = new TimeOnly(12, 0),
        EndTime = new TimeOnly(13, 0),
    };

    private static Expenses CreateTestExpenses(Guid workId, Guid? id = null)
    {
        var work = CreateTestWork(workId);
        return new Expenses
        {
            Id = id ?? Guid.NewGuid(),
            WorkId = workId,
            Work = work,
            Amount = 25.50m,
            Description = "Test Expenses",
            Taxable = true,
        };
    }

    private static WorkChange CreateTestWorkChange(Guid workId, Guid? replaceClientId = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        WorkId = workId,
        ChangeTime = 2,
        Surcharges = 0,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(10, 0),
        Type = WorkChangeType.CorrectionEnd,
        ReplaceClientId = replaceClientId,
        Description = "Test",
    };

    #region Work Handler - CompletionService Delegation

    [Test]
    public async Task Work_Post_ShouldCallAddAndSaveAndTrack()
    {
        // Arrange
        var workRepository = Substitute.For<IWorkRepository>();

        var shiftStatsService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
                Arg.Any<List<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftDayAssignment>());

        var handler = new WorkPostHandler(
            workRepository, _scheduleMapper, _notificationService,
            shiftStatsService, shiftScheduleService, _periodHoursService,
            _scheduleEntriesService, _completionService, _httpContextAccessor,
            Substitute.For<ILogger<WorkPostHandler>>());

        var resource = new WorkResource
        {
            ClientId = TestClientId,
            ShiftId = TestShiftId,
            CurrentDate = TestDate,
            WorkTime = 8,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
        };
        var command = new PostCommand<WorkResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workRepository.Received(1).Add(Arg.Any<Work>());
        await _completionService.Received(1)
            .SaveAndTrackAsync(Arg.Any<Guid>(), TestDate, PeriodStart, PeriodEnd);
    }

    [Test]
    public async Task Work_Put_ShouldCallPutAndSaveAndTrackMove()
    {
        // Arrange
        var workRepository = Substitute.For<IWorkRepository>();
        var testWork = CreateTestWork();
        workRepository.GetNoTracking(testWork.Id).Returns(testWork);
        workRepository.Put(Arg.Any<Work>()).Returns(testWork);

        var shiftStatsService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
                Arg.Any<List<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftDayAssignment>());

        var handler = new WorkPutHandler(
            workRepository, _scheduleMapper, _notificationService,
            shiftStatsService, shiftScheduleService, _periodHoursService,
            _scheduleEntriesService, _completionService, _httpContextAccessor,
            Substitute.For<ILogger<WorkPutHandler>>());

        var resource = new WorkResource
        {
            Id = testWork.Id,
            ClientId = TestClientId,
            ShiftId = TestShiftId,
            CurrentDate = TestDate,
            WorkTime = 8,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
        };
        var command = new PutCommand<WorkResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workRepository.Received(1).Put(Arg.Any<Work>());
        await _completionService.Received(1)
            .SaveAndTrackMoveAsync(TestClientId, TestDate, PeriodStart, PeriodEnd,
                Arg.Any<Guid?>(), Arg.Any<DateOnly?>());
    }

    [Test]
    public async Task Work_Delete_ShouldCallDeleteAndSaveAndTrack()
    {
        // Arrange
        var workRepository = Substitute.For<IWorkRepository>();
        var testWork = CreateTestWork();
        workRepository.Get(testWork.Id).Returns(testWork);

        var shiftStatsService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
                Arg.Any<List<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftDayAssignment>());

        var handler = new WorkDeleteHandler(
            workRepository, _scheduleMapper, _notificationService,
            shiftStatsService, shiftScheduleService, _scheduleEntriesService,
            _completionService, _httpContextAccessor,
            Substitute.For<ILogger<WorkDeleteHandler>>());

        var command = new DeleteWorkCommand(testWork.Id, PeriodStart, PeriodEnd);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workRepository.Received(1).Delete(testWork.Id);
        await _completionService.Received(1)
            .SaveAndTrackAsync(TestClientId, TestDate, PeriodStart, PeriodEnd);
    }

    #endregion

    #region Break Handler - CompletionService Delegation

    [Test]
    public async Task Break_Post_ShouldCallAddAndSaveAndTrack()
    {
        // Arrange
        var breakRepository = Substitute.For<IBreakRepository>();

        var handler = new BreakPostHandler(
            breakRepository, _scheduleMapper, _periodHoursService,
            _scheduleEntriesService, _notificationService, _completionService,
            _httpContextAccessor,
            Substitute.For<ILogger<BreakPostHandler>>());

        var resource = new BreakResource
        {
            ClientId = TestClientId,
            AbsenceId = TestAbsenceId,
            CurrentDate = TestDate,
            WorkTime = 1,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
        };
        var command = new PostCommand<BreakResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await breakRepository.Received(1)
            .Add(Arg.Any<Api.Domain.Models.Schedules.Break>());
        await _completionService.Received(1)
            .SaveAndTrackAsync(TestClientId, TestDate, PeriodStart, PeriodEnd);
    }

    [Test]
    public async Task Break_Put_ShouldCallPutAndSaveAndTrack()
    {
        // Arrange
        var breakRepository = Substitute.For<IBreakRepository>();
        var testBreak = CreateTestBreak();
        breakRepository.Put(Arg.Any<Api.Domain.Models.Schedules.Break>()).Returns(testBreak);

        var handler = new BreakPutHandler(
            breakRepository, _scheduleMapper, _periodHoursService,
            _scheduleEntriesService, _notificationService, _completionService,
            _httpContextAccessor,
            Substitute.For<ILogger<BreakPutHandler>>());

        var resource = new BreakResource
        {
            Id = testBreak.Id,
            ClientId = TestClientId,
            AbsenceId = TestAbsenceId,
            CurrentDate = TestDate,
            WorkTime = 1,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
        };
        var command = new PutCommand<BreakResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await breakRepository.Received(1)
            .Put(Arg.Any<Api.Domain.Models.Schedules.Break>());
        await _completionService.Received(1)
            .SaveAndTrackAsync(TestClientId, TestDate, PeriodStart, PeriodEnd);
    }

    [Test]
    public async Task Break_Delete_ShouldCallDeleteAndSaveAndTrack()
    {
        // Arrange
        var breakRepository = Substitute.For<IBreakRepository>();
        var testBreak = CreateTestBreak();
        breakRepository.Get(testBreak.Id).Returns(testBreak);

        var handler = new BreakDeleteHandler(
            breakRepository, _scheduleMapper, _scheduleEntriesService,
            _notificationService, _completionService, _httpContextAccessor,
            Substitute.For<ILogger<BreakDeleteHandler>>());

        var command = new DeleteBreakCommand(testBreak.Id, PeriodStart, PeriodEnd);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await breakRepository.Received(1).Delete(testBreak.Id);
        await _completionService.Received(1)
            .SaveAndTrackAsync(TestClientId, TestDate, PeriodStart, PeriodEnd);
    }

    #endregion

    #region Expenses Tracking

    [Test]
    public async Task Expenses_Post_ShouldTrackChange()
    {
        // Arrange
        var expensesRepository = Substitute.For<IExpensesRepository>();
        var workId = Guid.NewGuid();
        var testExpenses = CreateTestExpenses(workId);
        expensesRepository.Get(Arg.Any<Guid>()).Returns(testExpenses);

        var handler = new ExpensesPostHandler(
            expensesRepository, _scheduleMapper, _unitOfWork, _periodHoursService,
            _notificationService, _httpContextAccessor, _scheduleChangeTracker,
            Substitute.For<ILogger<ExpensesPostHandler>>());

        var resource = new ExpensesResource
        {
            WorkId = workId,
            Amount = 25.50m,
            Description = "Test",
            Taxable = true,
        };
        var command = new PostCommand<ExpensesResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await _scheduleChangeTracker.Received(1)
            .TrackChangeAsync(TestClientId, TestDate);
    }

    [Test]
    public async Task Expenses_Put_ShouldTrackChange()
    {
        // Arrange
        var expensesRepository = Substitute.For<IExpensesRepository>();
        var workId = Guid.NewGuid();
        var testExpenses = CreateTestExpenses(workId);
        expensesRepository.GetNoTracking(testExpenses.Id).Returns(testExpenses);
        expensesRepository.Put(Arg.Any<Expenses>()).Returns(testExpenses);
        expensesRepository.Get(testExpenses.Id).Returns(testExpenses);

        var handler = new ExpensesPutHandler(
            expensesRepository, _scheduleMapper, _unitOfWork, _periodHoursService,
            _notificationService, _httpContextAccessor, _scheduleChangeTracker,
            Substitute.For<ILogger<ExpensesPutHandler>>());

        var resource = new ExpensesResource
        {
            Id = testExpenses.Id,
            WorkId = workId,
            Amount = 30m,
            Description = "Updated",
            Taxable = true,
        };
        var command = new PutCommand<ExpensesResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await _scheduleChangeTracker.Received(1)
            .TrackChangeAsync(TestClientId, TestDate);
    }

    [Test]
    public async Task Expenses_Delete_ShouldTrackChange()
    {
        // Arrange
        var expensesRepository = Substitute.For<IExpensesRepository>();
        var workId = Guid.NewGuid();
        var testExpenses = CreateTestExpenses(workId);
        expensesRepository.Get(testExpenses.Id).Returns(testExpenses);

        var handler = new ExpensesDeleteHandler(
            expensesRepository, _scheduleMapper, _unitOfWork, _periodHoursService,
            _notificationService, _httpContextAccessor, _scheduleChangeTracker,
            Substitute.For<ILogger<ExpensesDeleteHandler>>());

        var command = new DeleteCommand<ExpensesResource>(testExpenses.Id);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await _scheduleChangeTracker.Received(1)
            .TrackChangeAsync(TestClientId, TestDate);
    }

    [Test]
    public async Task Expenses_Delete_WithoutWork_ShouldNotTrackChange()
    {
        // Arrange
        var expensesRepository = Substitute.For<IExpensesRepository>();
        var testExpenses = new Expenses
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Work = null,
            Amount = 10m,
            Description = "No work",
        };
        expensesRepository.Get(testExpenses.Id).Returns(testExpenses);

        var handler = new ExpensesDeleteHandler(
            expensesRepository, _scheduleMapper, _unitOfWork, _periodHoursService,
            _notificationService, _httpContextAccessor, _scheduleChangeTracker,
            Substitute.For<ILogger<ExpensesDeleteHandler>>());

        var command = new DeleteCommand<ExpensesResource>(testExpenses.Id);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await _scheduleChangeTracker.DidNotReceive()
            .TrackChangeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }

    #endregion

    #region WorkChange Handler - Repository Delegation

    [Test]
    public async Task WorkChange_Post_ShouldCallRepositoryAdd()
    {
        // Arrange
        var workChangeRepository = Substitute.For<IWorkChangeRepository>();
        var workRepository = Substitute.For<IWorkRepository>();
        var testWork = CreateTestWork();

        workRepository.Get(testWork.Id).Returns(testWork);

        var handler = new WorkChangePostHandler(
            workChangeRepository, workRepository, _scheduleMapper,
            _periodHoursService, _scheduleEntriesService, _notificationService,
            _completionService, _httpContextAccessor,
            Substitute.For<ILogger<WorkChangePostHandler>>());

        var resource = new WorkChangeResource
        {
            WorkId = testWork.Id,
            ChangeTime = 2,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            Type = WorkChangeType.CorrectionEnd,
        };
        var command = new PostCommand<WorkChangeResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workChangeRepository.Received(1)
            .Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task WorkChange_Put_ShouldCallRepositoryPut()
    {
        // Arrange
        var workChangeRepository = Substitute.For<IWorkChangeRepository>();
        var workRepository = Substitute.For<IWorkRepository>();
        var testWork = CreateTestWork();
        var testWorkChange = CreateTestWorkChange(testWork.Id);

        workChangeRepository.Get(testWorkChange.Id).Returns(testWorkChange);
        workChangeRepository.Put(Arg.Any<WorkChange>()).Returns(testWorkChange);
        workRepository.Get(testWork.Id).Returns(testWork);

        var handler = new WorkChangePutHandler(
            workChangeRepository, workRepository, _scheduleMapper,
            _periodHoursService, _scheduleEntriesService, _notificationService,
            _completionService, _httpContextAccessor,
            Substitute.For<ILogger<WorkChangePutHandler>>());

        var resource = new WorkChangeResource
        {
            Id = testWorkChange.Id,
            WorkId = testWork.Id,
            ChangeTime = 2,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            Type = WorkChangeType.CorrectionEnd,
        };
        var command = new PutCommand<WorkChangeResource>(resource);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workChangeRepository.Received(1)
            .Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task WorkChange_Delete_ShouldCallRepositoryDelete()
    {
        // Arrange
        var workChangeRepository = Substitute.For<IWorkChangeRepository>();
        var workRepository = Substitute.For<IWorkRepository>();
        var testWork = CreateTestWork();
        var testWorkChange = CreateTestWorkChange(testWork.Id);

        workChangeRepository.Get(testWorkChange.Id).Returns(testWorkChange);
        workChangeRepository.Delete(testWorkChange.Id).Returns(testWorkChange);
        workRepository.Get(testWork.Id).Returns(testWork);

        var handler = new WorkChangeDeleteHandler(
            workChangeRepository, workRepository, _scheduleMapper,
            _periodHoursService, _scheduleEntriesService, _notificationService,
            _completionService, _httpContextAccessor,
            Substitute.For<ILogger<WorkChangeDeleteHandler>>());

        var command = new DeleteCommand<WorkChangeResource>(testWorkChange.Id);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workChangeRepository.Received(1)
            .Delete(testWorkChange.Id);
    }

    #endregion

    #region BulkAdd/BulkDelete Work - CompletionService Delegation

    [Test]
    public async Task BulkAddWorks_ShouldCallAddAndSaveBulk()
    {
        // Arrange
        var workRepository = Substitute.For<IWorkRepository>();

        var shiftStatsService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
                Arg.Any<List<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftDayAssignment>());

        var handler = new BulkAddWorksCommandHandler(
            workRepository, _scheduleMapper, _notificationService,
            shiftStatsService, shiftScheduleService, _periodHoursService,
            _completionService, _httpContextAccessor,
            Substitute.For<ILogger<BulkAddWorksCommandHandler>>());

        var clientId2 = Guid.NewGuid();
        var date2 = new DateOnly(2026, 2, 21);

        var request = new BulkAddWorksRequest
        {
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            Works =
            [
                new BulkWorkItem
                {
                    ClientId = TestClientId, ShiftId = TestShiftId, CurrentDate = TestDate,
                    WorkTime = 8, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0),
                },
                new BulkWorkItem
                {
                    ClientId = clientId2, ShiftId = TestShiftId, CurrentDate = date2,
                    WorkTime = 4, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(12, 0),
                },
            ],
        };
        var command = new BulkAddWorksCommand(request);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workRepository.Received(2).Add(Arg.Any<Work>());
        await _completionService.Received(1)
            .SaveBulkAndTrackAsync(Arg.Is<List<(Guid, DateOnly)>>(l => l.Count == 2));
    }

    [Test]
    public async Task BulkDeleteWorks_ShouldCallDeleteAndSaveBulk()
    {
        // Arrange
        var workRepository = Substitute.For<IWorkRepository>();
        var work1 = CreateTestWork();
        var work2Id = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();
        var date2 = new DateOnly(2026, 2, 21);
        var work2 = new Work
        {
            Id = work2Id, ClientId = clientId2, ShiftId = TestShiftId, CurrentDate = date2,
            WorkTime = 4, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(12, 0),
        };

        workRepository.Get(work1.Id).Returns(work1);
        workRepository.Get(work2.Id).Returns(work2);

        var shiftStatsService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
                Arg.Any<List<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftDayAssignment>());

        var handler = new BulkDeleteWorksCommandHandler(
            workRepository, _scheduleMapper, _notificationService,
            shiftStatsService, shiftScheduleService, _periodHoursService,
            _completionService, _httpContextAccessor,
            Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());

        var request = new BulkDeleteWorksRequest { WorkIds = [work1.Id, work2.Id] };
        var command = new BulkDeleteWorksCommand(request);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await workRepository.Received(2).Delete(Arg.Any<Guid>());
        await _completionService.Received(1)
            .SaveBulkAndTrackAsync(Arg.Is<List<(Guid, DateOnly)>>(l => l.Count == 2));
    }

    #endregion

    #region BulkAdd/BulkDelete Break - CompletionService Delegation

    [Test]
    public async Task BulkAddBreaks_ShouldCallAddAndSaveBulk()
    {
        // Arrange
        var breakRepository = Substitute.For<IBreakRepository>();

        var handler = new BulkAddBreaksCommandHandler(
            breakRepository, _periodHoursService, _completionService,
            Substitute.For<ILogger<BulkAddBreaksCommandHandler>>());

        var clientId2 = Guid.NewGuid();
        var date2 = new DateOnly(2026, 2, 21);

        var request = new BulkAddBreaksRequest
        {
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            Breaks =
            [
                new BulkBreakItem
                {
                    ClientId = TestClientId, AbsenceId = TestAbsenceId, CurrentDate = TestDate,
                    WorkTime = 1, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(13, 0),
                },
                new BulkBreakItem
                {
                    ClientId = clientId2, AbsenceId = TestAbsenceId, CurrentDate = date2,
                    WorkTime = 0.5m, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(12, 30),
                },
            ],
        };
        var command = new BulkAddBreaksCommand(request);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await breakRepository.Received(2)
            .Add(Arg.Any<Api.Domain.Models.Schedules.Break>());
        await _completionService.Received(1)
            .SaveBulkAndTrackAsync(Arg.Is<List<(Guid, DateOnly)>>(l => l.Count == 2));
    }

    [Test]
    public async Task BulkDeleteBreaks_ShouldCallDeleteAndSaveBulk()
    {
        // Arrange
        var breakRepository = Substitute.For<IBreakRepository>();
        var break1 = CreateTestBreak();
        var break2Id = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();
        var date2 = new DateOnly(2026, 2, 21);
        var break2 = new Api.Domain.Models.Schedules.Break
        {
            Id = break2Id, ClientId = clientId2, AbsenceId = TestAbsenceId, CurrentDate = date2,
            WorkTime = 0.5m, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(12, 30),
        };

        breakRepository.Get(break1.Id).Returns(break1);
        breakRepository.Get(break2.Id).Returns(break2);

        var handler = new BulkDeleteBreaksCommandHandler(
            breakRepository, _periodHoursService, _completionService,
            Substitute.For<ILogger<BulkDeleteBreaksCommandHandler>>());

        var request = new BulkDeleteBreaksRequest
        {
            BreakIds = [break1.Id, break2.Id],
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
        };
        var command = new BulkDeleteBreaksCommand(request);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        await breakRepository.Received(2).Delete(Arg.Any<Guid>());
        await _completionService.Received(1)
            .SaveBulkAndTrackAsync(Arg.Is<List<(Guid, DateOnly)>>(l => l.Count == 2));
    }

    #endregion
}
