// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the Expenses endpoints ignored group visibility: any user could list every expense of
/// the tenant, read one by id, and create, edit or delete expenses on shifts of employees outside their
/// groups. An expense whose parent Work is owned by a hidden client must now be answered exactly like a
/// missing one, and nothing may be written for it.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Expenses;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ExpensesEntity = Klacks.Api.Domain.Models.Schedules.Expenses;

namespace Klacks.UnitTest.Application.Handlers.Expenses;

[TestFixture]
public class ExpensesVisibilityTests
{
    private static readonly DateOnly Day = new(2027, 6, 7);

    private IExpensesRepository _expensesRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDayLockService _dayLockService = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _expensesRepository = Substitute.For<IExpensesRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _dayLockService = Substitute.For<IDayLockService>();

        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClientId));
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ExpensesEntity>>(), Arg.Any<Func<ExpensesEntity, Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<ExpensesEntity>>(0)
                .Where(e => ci.ArgAt<Func<ExpensesEntity, Guid>>(1)(e) != _hiddenClientId)
                .ToList());
    }

    [Test]
    public async Task List_LeavesOutExpensesOnWorksOfHiddenClients()
    {
        var visibleWork = NewWork(_visibleClientId);
        var hiddenWork = NewWork(_hiddenClientId);
        var visible = NewExpense(visibleWork);
        var hidden = NewExpense(hiddenWork);
        _expensesRepository.ListInScopeAsync(null, Arg.Any<CancellationToken>())
            .Returns(new List<ExpensesEntity> { visible, hidden });
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { visibleWork, hiddenWork });
        var handler = new ListQueryHandler(
            _expensesRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<ILogger<ListQueryHandler>>());

        var result = (await handler.Handle(new ListQuery<ExpensesResource>(), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visible.Id });
    }

    [Test]
    public async Task List_ForAScenario_ReadsOnlyThatScope_AndStillLeavesOutHiddenClients()
    {
        var token = Guid.NewGuid();
        var visibleWork = NewWork(_visibleClientId);
        var hiddenWork = NewWork(_hiddenClientId);
        var visible = NewExpense(visibleWork);
        var hidden = NewExpense(hiddenWork);
        _expensesRepository.ListInScopeAsync(token, Arg.Any<CancellationToken>())
            .Returns(new List<ExpensesEntity> { visible, hidden });
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { visibleWork, hiddenWork });
        var handler = new ListQueryHandler(
            _expensesRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<ILogger<ListQueryHandler>>());

        var result = (await handler.Handle(
            new Klacks.Api.Application.Queries.Schedules.ListExpensesInScopeQuery(token), CancellationToken.None)).ToList();

        result.Select(r => r.Id).ShouldBe(new[] { visible.Id });
        await _expensesRepository.DidNotReceive().ListInScopeAsync(null, Arg.Any<CancellationToken>());
        await _expensesRepository.DidNotReceive().List();
    }

    [Test]
    public async Task List_OfTheMainPlan_NeverReadsAScenarioScope()
    {
        _expensesRepository.ListInScopeAsync(null, Arg.Any<CancellationToken>()).Returns(new List<ExpensesEntity>());
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work>());
        var handler = new ListQueryHandler(
            _expensesRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<ILogger<ListQueryHandler>>());

        await handler.Handle(new ListQuery<ExpensesResource>(), CancellationToken.None);

        await _expensesRepository.Received(1).ListInScopeAsync(null, Arg.Any<CancellationToken>());
        await _expensesRepository.DidNotReceive().List();
    }

    [Test]
    public async Task Get_ExpenseOnAWorkOfAHiddenClient_IsAnsweredLikeAMissingExpense()
    {
        var hidden = NewExpense(NewWork(_hiddenClientId));
        var missingId = Guid.NewGuid();
        _expensesRepository.Get(hidden.Id).Returns(hidden);
        _expensesRepository.Get(missingId).Returns((ExpensesEntity?)null);
        var handler = new GetQueryHandler(
            _expensesRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var hiddenEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<ExpensesResource>(hidden.Id), CancellationToken.None));
        var missingEx = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<ExpensesResource>(missingId), CancellationToken.None));

        hiddenEx.Message.ShouldBe($"Expenses with ID {hidden.Id} not found");
        missingEx.Message.ShouldBe($"Expenses with ID {missingId} not found");
    }

    [Test]
    public async Task Post_OnAWorkOfAHiddenClient_IsRefusedLikeAMissingWork_NothingWritten()
    {
        var hiddenWork = NewWork(_hiddenClientId);
        _workRepository.GetNoTracking(hiddenWork.Id).Returns(hiddenWork);
        var handler = new PostCommandHandler(
            _expensesRepository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleEntriesService>(),
            Substitute.For<IWorkNotificationService>(), Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IScheduleChangeTracker>(), Substitute.For<ISelectedGroupContextResolver>(),
            _workRepository, _dayLockService, Substitute.For<IParentWorkLockGuard>(),
            Substitute.For<ILogger<PostCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PostCommand<ExpensesResource>(NewResource(Guid.NewGuid(), hiddenWork.Id)), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {hiddenWork.Id} not found");
        ex.Message.ShouldNotContain(_hiddenClientId.ToString());
        await _expensesRepository.DidNotReceive().Add(Arg.Any<ExpensesEntity>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_OfAnExpenseOnAWorkOfAHiddenClient_IsAnsweredLikeAMissingExpense_NothingWritten()
    {
        var hiddenWork = NewWork(_hiddenClientId);
        var stored = NewExpense(hiddenWork);
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(hiddenWork.Id).Returns(hiddenWork);

        var result = await NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, hiddenWork.Id)), CancellationToken.None);

        result.ShouldBeNull();
        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_MovingAnExpenseOntoAWorkOfAHiddenClient_IsAnsweredLikeAMissingExpense_NothingWritten()
    {
        var visibleWork = NewWork(_visibleClientId);
        var hiddenWork = NewWork(_hiddenClientId);
        var stored = NewExpense(visibleWork);
        _expensesRepository.GetNoTracking(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(visibleWork.Id).Returns(visibleWork);
        _workRepository.GetNoTracking(hiddenWork.Id).Returns(hiddenWork);

        var result = await NewPutHandler().Handle(
            new PutCommand<ExpensesResource>(NewResource(stored.Id, hiddenWork.Id)), CancellationToken.None);

        result.ShouldBeNull();
        await _expensesRepository.DidNotReceive().Put(Arg.Any<ExpensesEntity>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_ExpenseOnAWorkOfAHiddenClient_IsAnsweredLikeAMissingExpense_NothingDeleted()
    {
        var stored = NewExpense(NewWork(_hiddenClientId));
        _expensesRepository.GetWithWorkInAnyScope(stored.Id).Returns(stored);
        var handler = new DeleteCommandHandler(
            _expensesRepository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleEntriesService>(),
            Substitute.For<IWorkNotificationService>(), Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IScheduleChangeTracker>(), Substitute.For<ISelectedGroupContextResolver>(),
            _dayLockService, Substitute.For<IParentWorkLockGuard>(), Substitute.For<ILogger<DeleteCommandHandler>>());

        var result = await handler.Handle(new DeleteCommand<ExpensesResource>(stored.Id), CancellationToken.None);

        result.ShouldBeNull();
        await _expensesRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private PutCommandHandler NewPutHandler()
        => new(
            _expensesRepository, _clientVisibilityGuard, new ScheduleMapper(), _unitOfWork,
            Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleEntriesService>(),
            Substitute.For<IWorkNotificationService>(), Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IScheduleChangeTracker>(), Substitute.For<ISelectedGroupContextResolver>(),
            _workRepository, _dayLockService, Substitute.For<IParentWorkLockGuard>(),
            Substitute.For<ILogger<PutCommandHandler>>());

    private static Work NewWork(Guid clientId)
        => new() { Id = Guid.NewGuid(), ClientId = clientId, ShiftId = Guid.NewGuid(), CurrentDate = Day };

    private static ExpensesEntity NewExpense(Work work)
        => new() { Id = Guid.NewGuid(), WorkId = work.Id, Work = work };

    private static ExpensesResource NewResource(Guid id, Guid workId)
        => new() { Id = id, WorkId = workId };
}
