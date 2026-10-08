// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// WP1 expense sealing gaps (2026-10-08): expenses ignored the lock level of their parent Work (a legacy period
/// close writes no SealedDay rows, so the day lock never stopped them), the request-less AnalyseToken was wiped
/// on every PUT (a scenario expense jumped into the main plan), and scenario expenses could not be deleted (404).
/// These tests pin the parent-Work lock, the server-side token, and the scenario delete.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Expenses;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.Application.Handlers.Works;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ExpensesEntity = Klacks.Api.Domain.Models.Schedules.Expenses;

namespace Klacks.UnitTest.Application.Handlers.Expenses;

[TestFixture]
public class ExpensesSealAndScopeTests
{
    private const string UserName = "test-user";
    private static readonly DateOnly Day = new(2027, 6, 7);

    private IExpensesRepository _expensesRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDayLockService _dayLockService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _expensesRepository = Substitute.For<IExpensesRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _dayLockService = Substitute.For<IDayLockService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Day, Day));
        _scheduleEntriesService.GetScheduleEntriesQuery(default, default, default, default)
            .ReturnsForAnyArgs(_ => new TestAsyncEnumerable<ScheduleCell>(Enumerable.Empty<ScheduleCell>()));
        _expensesRepository.Put(Arg.Any<ExpensesEntity>()).Returns(ci => ci.ArgAt<ExpensesEntity>(0));

        WorksTestHelpers.GivenUserIsAdmin(_httpContextAccessor, UserName);
    }

    [Test]
    public async Task Post_OnAClosedWork_IsRefusedEvenForAnAdmin_NothingWritten()
    {
        var work = NewWork(WorkLockLevel.Closed, null);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPostHandler().Handle(
            new PostCommand<ExpensesResource>(NewResource(Guid.NewGuid(), work.Id)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedParentMessage);
        await _expensesRepository.DidNotReceive().Add(Arg.Any<ExpensesEntity>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_OnAMissingWork_IsRefusedLikeAHiddenWork_NothingWritten()
    {
        var missingWorkId = Guid.NewGuid();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => NewPostHandler().Handle(
            new PostCommand<ExpensesResource>(NewResource(Guid.NewGuid(), missingWorkId)), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {missingWorkId} not found");
        await _expensesRepository.DidNotReceive().Add(Arg.Any<ExpensesEntity>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_OnAnApprovedWork_AsRegularUser_IsRefused_NothingWritten()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, UserName);
        var work = NewWork(WorkLockLevel.Approved, null);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPostHandler().Handle(
            new PostCommand<ExpensesResource>(NewResource(Guid.NewGuid(), work.Id)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.SealedParentMessage);
        await _expensesRepository.DidNotReceive().Add(Arg.Any<ExpensesEntity>());
    }

    [Test]
    public async Task Post_OnAnApprovedWork_AsAuthorised_IsWritten()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, UserName);
        var work = NewWork(WorkLockLevel.Approved, null);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        await NewPostHandler().Handle(
            new PostCommand<ExpensesResource>(NewResource(Guid.NewGuid(), work.Id)), CancellationToken.None);

        await _expensesRepository.Received(1).Add(Arg.Any<ExpensesEntity>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Post_OnAScenarioWork_InheritsTheWorksToken_AndTheDayLockSeesTheScenario()
    {
        var token = Guid.NewGuid();
        var work = NewWork(WorkLockLevel.Closed, token);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        await NewPostHandler().Handle(
            new PostCommand<ExpensesResource>(NewResource(Guid.NewGuid(), work.Id)), CancellationToken.None);

        await _expensesRepository.Received(1).Add(Arg.Is<ExpensesEntity>(e => e.AnalyseToken == token));
        await _dayLockService.Received(1).EnsureNotLockedAsync(work.CurrentDate, work.ClientId, token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_OnAClosedWork_IsRefusedEvenForAnAdmin_NothingWritten()
    {
        var work = NewWork(WorkLockLevel.Closed, null);
        var stored = NewExpense(work);
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, work.Id)), CancellationToken.None));

        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_MovingAwayFromAClosedWork_IsRefused_NothingWritten()
    {
        var closedWork = NewWork(WorkLockLevel.Closed, null);
        var openWork = NewWork(WorkLockLevel.None, null);
        var stored = NewExpense(closedWork);
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(closedWork.Id).Returns(closedWork);
        _workRepository.GetNoTracking(openWork.Id).Returns(openWork);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, openWork.Id)), CancellationToken.None));

        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
    }

    [Test]
    public async Task Put_OfAScenarioExpense_KeepsTheStoredToken_AndRecalculatesTheScenario()
    {
        var token = Guid.NewGuid();
        var work = NewWork(WorkLockLevel.None, token);
        var stored = NewExpense(work);
        stored.AnalyseToken = token;
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _expensesRepository.GetWithWorkInAnyScope(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        var result = await NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        result.ShouldNotBeNull();
        await _expensesRepository.Received(1).Put(Arg.Is<ExpensesEntity>(e => e.AnalyseToken == token));
        await _dayLockService.Received(1).EnsureNotLockedAsync(work.CurrentDate, work.ClientId, token, Arg.Any<CancellationToken>());
        await _periodHoursService.Received(1).RecalculateAndNotifyAsync(
            work.ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), token, Arg.Any<string?>());
    }

    [Test]
    public async Task Put_WithAStoredNullTokenOnAScenarioParent_TakesTheParentsToken()
    {
        var token = Guid.NewGuid();
        var work = NewWork(WorkLockLevel.None, token);
        var stored = NewExpense(work);
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        await NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        await _expensesRepository.Received(1).Put(Arg.Is<ExpensesEntity>(e => e.AnalyseToken == token));
    }

    [Test]
    public async Task Put_MovingIntoAClosedWork_IsRefused_NothingWritten()
    {
        var openWork = NewWork(WorkLockLevel.None, null);
        var closedWork = NewWork(WorkLockLevel.Closed, null);
        var stored = NewExpense(openWork);
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(openWork.Id).Returns(openWork);
        _workRepository.GetNoTracking(closedWork.Id).Returns(closedWork);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, closedWork.Id)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedParentMessage);
        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
    }

    [Test]
    public async Task Put_WhenTheNewParentWorkIsMissing_IsAnsweredLikeAMissingExpense_NothingWritten()
    {
        var work = NewWork(WorkLockLevel.None, null);
        var stored = NewExpense(work);
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        var result = await NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, Guid.NewGuid())), CancellationToken.None);

        result.ShouldBeNull();
        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
    }

    [Test]
    public async Task Put_WhenTheOldParentWorkIsMissingOnAMove_IsAnsweredLikeAMissingExpense_NothingWritten()
    {
        var newWork = NewWork(WorkLockLevel.None, null);
        var stored = new ExpensesEntity { Id = Guid.NewGuid(), WorkId = Guid.NewGuid() };
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(newWork.Id).Returns(newWork);

        var result = await NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, newWork.Id)), CancellationToken.None);

        result.ShouldBeNull();
        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
    }

    [Test]
    public async Task Put_MovingAScenarioExpenseOntoAMainPlanWork_IsRefused_NothingWritten()
    {
        var token = Guid.NewGuid();
        var scenarioWork = NewWork(WorkLockLevel.None, token);
        var realWork = NewWork(WorkLockLevel.None, null);
        var stored = NewExpense(scenarioWork);
        stored.AnalyseToken = token;
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(scenarioWork.Id).Returns(scenarioWork);
        _workRepository.GetNoTracking(realWork.Id).Returns(realWork);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, realWork.Id)), CancellationToken.None));

        ex.Message.ShouldBe(PutCommandHandler.CrossScopeMoveMessage);
        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
    }

    [Test]
    public async Task Delete_OfAScenarioExpense_IsReachedAndDeleted()
    {
        var token = Guid.NewGuid();
        var work = NewWork(WorkLockLevel.None, token);
        var stored = NewExpense(work);
        stored.AnalyseToken = token;
        _expensesRepository.GetWithWorkInAnyScope(stored.Id).Returns(stored);

        var result = await NewDeleteHandler().Handle(new DeleteCommand<ExpensesResource>(stored.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _expensesRepository.Received(1).Delete(stored.Id);
        await _dayLockService.Received(1).EnsureNotLockedAsync(work.CurrentDate, work.ClientId, token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_OnAClosedWork_IsRefusedEvenForAnAdmin_NothingDeleted()
    {
        var work = NewWork(WorkLockLevel.Closed, null);
        var stored = NewExpense(work);
        _expensesRepository.GetWithWorkInAnyScope(stored.Id).Returns(stored);

        await Should.ThrowAsync<InvalidRequestException>(
            () => NewDeleteHandler().Handle(new DeleteCommand<ExpensesResource>(stored.Id), CancellationToken.None));

        await _expensesRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private ParentWorkLockGuard NewGuard() => new(new WorkLockLevelService());

    private PostCommandHandler NewPostHandler()
        => new(
            _expensesRepository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            _periodHoursService, _scheduleEntriesService,
            Substitute.For<IWorkNotificationService>(), _httpContextAccessor,
            Substitute.For<IScheduleChangeTracker>(), Substitute.For<ISelectedGroupContextResolver>(),
            _workRepository, _dayLockService, NewGuard(), Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler NewPutHandler()
        => new(
            _expensesRepository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            _periodHoursService, _scheduleEntriesService,
            Substitute.For<IWorkNotificationService>(), _httpContextAccessor,
            Substitute.For<IScheduleChangeTracker>(), Substitute.For<ISelectedGroupContextResolver>(),
            _workRepository, _dayLockService, NewGuard(), Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler NewDeleteHandler()
        => new(
            _expensesRepository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            _periodHoursService, _scheduleEntriesService,
            Substitute.For<IWorkNotificationService>(), _httpContextAccessor,
            Substitute.For<IScheduleChangeTracker>(), Substitute.For<ISelectedGroupContextResolver>(),
            _dayLockService, NewGuard(), Substitute.For<ILogger<DeleteCommandHandler>>());

    private static Work NewWork(WorkLockLevel level, Guid? analyseToken)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            LockLevel = level,
            AnalyseToken = analyseToken
        };

    private static ExpensesEntity NewExpense(Work work)
        => new() { Id = Guid.NewGuid(), WorkId = work.Id, Work = work };

    private static ExpensesResource NewResource(Guid id, Guid workId)
        => new() { Id = id, WorkId = workId, Amount = 12.5m, Description = "Train ticket" };
}
