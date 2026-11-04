// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner 2026-11-04: expenses belong to the container, never to a service inside it. Posting an expense on a container
/// service, or moving one onto it, is refused with a coded 400 before anything is written; the container itself takes them.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ExpensesEntity = Klacks.Api.Domain.Models.Schedules.Expenses;
using ExpensesPostHandler = Klacks.Api.Application.Handlers.Expenses.PostCommandHandler;
using ExpensesPutHandler = Klacks.Api.Application.Handlers.Expenses.PutCommandHandler;

namespace Klacks.UnitTest.Application.Handlers.Expenses;

[TestFixture]
public class ContainerServiceExpenseTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);

    private IExpensesRepository _expensesRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IHttpContextAccessor _http = null!;
    private List<ExpensesEntity> _added = null!;
    private Work _container = null!;
    private Work _inner = null!;

    [SetUp]
    public void SetUp()
    {
        _container = new Work { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), CurrentDate = Day };
        _inner = new Work { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ParentWorkId = _container.Id, CurrentDate = Day };
        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.GetNoTracking(_container.Id).Returns(_container);
        _workRepository.GetNoTracking(_inner.Id).Returns(_inner);
        _expensesRepository = Substitute.For<IExpensesRepository>();
        _added = [];
        _expensesRepository.Add(Arg.Do<ExpensesEntity>(e => _added.Add(e))).Returns(Task.CompletedTask);
        _expensesRepository.GetWithWorkInAnyScope(Arg.Any<Guid>()).Returns((ExpensesEntity?)null);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _http = Substitute.For<IHttpContextAccessor>();
        _http.HttpContext.Returns(new DefaultHttpContext());
    }

    private ExpensesPostHandler PostHandler() => new(
        _expensesRepository, TestScheduleRowAccess.Over(TestGroupWriteVisibility.AllClientsVisible()), new ScheduleMapper(),
        _unitOfWork, Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleEntriesService>(),
        Substitute.For<IWorkNotificationService>(), _http, Substitute.For<IScheduleChangeTracker>(),
        Substitute.For<ISelectedGroupContextResolver>(), _workRepository, Substitute.For<IDayLockService>(),
        new ParentWorkLockGuard(new WorkLockLevelService()), Substitute.For<ILogger<ExpensesPostHandler>>());

    private ExpensesPutHandler PutHandler() => new(
        _expensesRepository, TestScheduleRowAccess.Over(TestGroupWriteVisibility.AllClientsVisible()), new ScheduleMapper(),
        _unitOfWork, Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleEntriesService>(),
        Substitute.For<IWorkNotificationService>(), _http, Substitute.For<IScheduleChangeTracker>(),
        Substitute.For<ISelectedGroupContextResolver>(), _workRepository, Substitute.For<IDayLockService>(),
        new ParentWorkLockGuard(new WorkLockLevelService()), Substitute.For<ILogger<ExpensesPutHandler>>());

    [Test]
    public async Task ExpenseOnAServiceInsideAContainer_IsRefused_AndNothingIsWritten()
    {
        await Should.ThrowAsync<ContainerServiceExpenseRefusedException>(() => PostHandler().Handle(
            new PostCommand<ExpensesResource>(new ExpensesResource { WorkId = _inner.Id, Amount = 12m, Description = "Parking" }),
            CancellationToken.None));

        _added.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ExpenseOnTheContainer_IsWritten()
    {
        await PostHandler().Handle(
            new PostCommand<ExpensesResource>(new ExpensesResource { WorkId = _container.Id, Amount = 12m, Description = "Parking" }),
            CancellationToken.None);

        _added.Single().WorkId.ShouldBe(_container.Id);
    }

    [Test]
    public async Task MovingAnExpenseOntoAServiceInsideAContainer_IsRefused()
    {
        var expenseId = Guid.NewGuid();
        _expensesRepository.GetNoTracking(expenseId).Returns(new ExpensesEntity { Id = expenseId, WorkId = _container.Id, Amount = 5m });

        await Should.ThrowAsync<ContainerServiceExpenseRefusedException>(() => PutHandler().Handle(
            new PutCommand<ExpensesResource>(new ExpensesResource { Id = expenseId, WorkId = _inner.Id, Amount = 5m }),
            CancellationToken.None));

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
