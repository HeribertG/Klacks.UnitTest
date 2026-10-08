// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// WP1 (2026-10-08): WorkChangeResource carries no AnalyseToken and PUT writes the full row, so every edit of a
/// scenario WorkChange wiped its token and moved it into the main plan; DELETE could not reach scenario changes; and
/// WorkChanges (payroll relevant) ignored the lock level of their parent Work. The token now follows the parent Work,
/// cross-scope moves are refused, a missing parent answers like a missing change, DELETE loads changes in any scope,
/// and Post/Put/Delete apply the same parent-Work lock rule as expenses.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.Application.Handlers.Works;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.WorkChanges;

[TestFixture]
public class WorkChangeScopeTests
{
    private const string UserName = "test-user";
    private static readonly DateOnly Day = new(2027, 5, 10);

    private IWorkChangeRepository _workChangeRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IDayLockService _dayLockService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _dayLockService = Substitute.For<IDayLockService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _workChangeRepository.Put(Arg.Any<WorkChange>()).Returns(ci => ci.ArgAt<WorkChange>(0));

        WorksTestHelpers.GivenUserIsAdmin(_httpContextAccessor, UserName);
    }

    [Test]
    public async Task Put_OfAScenarioChange_KeepsTheScenarioToken()
    {
        var token = Guid.NewGuid();
        var work = NewWork(token);
        var stored = NewChange(work.Id, token);
        Given(stored, work);

        await NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        await _workChangeRepository.Received(1).Put(Arg.Is<WorkChange>(c => c.AnalyseToken == token));
        await _dayLockService.Received(1).EnsureNotLockedAsync(work.CurrentDate, work.ClientId, token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_WithAStoredNullTokenOnAScenarioParent_TakesTheParentsToken()
    {
        var token = Guid.NewGuid();
        var work = NewWork(token);
        var stored = NewChange(work.Id, null);
        Given(stored, work);

        await NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None);

        await _workChangeRepository.Received(1).Put(Arg.Is<WorkChange>(c => c.AnalyseToken == token));
    }

    [Test]
    public async Task Put_MovingAScenarioChangeOntoAMainPlanWork_IsRefused_NothingWritten()
    {
        var token = Guid.NewGuid();
        var scenarioWork = NewWork(token);
        var realWork = NewWork(null);
        var stored = NewChange(scenarioWork.Id, token);
        Given(stored, scenarioWork, realWork);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, realWork.Id)), CancellationToken.None));

        ex.Message.ShouldBe(PutCommandHandler.CrossScopeMoveMessage);
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Put_WhenTheNewParentWorkIsMissing_IsAnsweredLikeAMissingChange_NothingWritten()
    {
        var work = NewWork(null);
        var stored = NewChange(work.Id, null);
        Given(stored, work);

        var result = await NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, Guid.NewGuid())), CancellationToken.None);

        result.ShouldBeNull();
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
    }

    [Test]
    public async Task Put_WhenTheOldParentWorkIsMissingOnAMove_IsAnsweredLikeAMissingChange_NothingWritten()
    {
        var newWork = NewWork(null);
        var stored = NewChange(Guid.NewGuid(), null);
        Given(stored, newWork);

        var result = await NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, newWork.Id)), CancellationToken.None);

        result.ShouldBeNull();
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Put_OnAClosedParent_IsRefusedEvenForAnAdmin_NothingWritten()
    {
        var work = NewWork(null, WorkLockLevel.Closed);
        var stored = NewChange(work.Id, null);
        Given(stored, work);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedParentMessage);
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Put_MovingAwayFromAClosedParent_IsRefused_NothingWritten()
    {
        var closedWork = NewWork(null, WorkLockLevel.Closed);
        var openWork = NewWork(null);
        var stored = NewChange(closedWork.Id, null);
        Given(stored, closedWork, openWork);

        await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, openWork.Id)), CancellationToken.None));

        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Put_OnAnApprovedParent_AsRegularUser_IsRefused_NothingWritten()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, UserName);
        var work = NewWork(null, WorkLockLevel.Approved);
        var stored = NewChange(work.Id, null);
        Given(stored, work);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkChangeResource>(NewResource(stored.Id, work.Id)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.SealedParentMessage);
        await _workChangeRepository.DidNotReceive().Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Post_OnAClosedParent_IsRefusedEvenForAnAdmin_NothingWritten()
    {
        var work = NewWork(null, WorkLockLevel.Closed);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPostHandler().Handle(
            new PostCommand<WorkChangeResource>(NewResource(Guid.NewGuid(), work.Id)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedParentMessage);
        await _workChangeRepository.DidNotReceive().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Post_OnAConfirmedParent_AsRegularUser_IsNotRefusedByTheLock()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, UserName);
        var work = NewWork(null, WorkLockLevel.Confirmed);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        await Should.NotThrowAsync(() => NewPostHandler().Handle(
            new PostCommand<WorkChangeResource>(NewResource(Guid.NewGuid(), work.Id)), CancellationToken.None));

        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Delete_OfAScenarioChange_IsReachedAndDeleted()
    {
        var token = Guid.NewGuid();
        var work = NewWork(token, WorkLockLevel.Closed);
        var stored = NewChange(work.Id, token);
        _workChangeRepository.GetWithWorkInAnyScope(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        await NewDeleteHandler().Handle(new DeleteCommand<WorkChangeResource>(stored.Id), CancellationToken.None);

        await _workChangeRepository.Received(1).Delete(stored.Id);
        await _dayLockService.Received(1).EnsureNotLockedAsync(work.CurrentDate, work.ClientId, token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_OnAClosedParent_IsRefusedEvenForAnAdmin_NothingDeleted()
    {
        var work = NewWork(null, WorkLockLevel.Closed);
        var stored = NewChange(work.Id, null);
        _workChangeRepository.GetWithWorkInAnyScope(stored.Id).Returns(stored);
        _workRepository.GetNoTracking(work.Id).Returns(work);

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => NewDeleteHandler().Handle(new DeleteCommand<WorkChangeResource>(stored.Id), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedParentMessage);
        await _workChangeRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    private void Given(WorkChange stored, params Work[] works)
    {
        _workChangeRepository.GetNoTracking(stored.Id).Returns(stored);
        foreach (var work in works)
        {
            _workRepository.GetNoTracking(work.Id).Returns(work);
        }
    }

    private static ParentWorkLockGuard NewGuard() => new(new WorkLockLevelService());

    private PutCommandHandler NewPutHandler()
        => new(
            _workChangeRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleCompletionService>(),
            Substitute.For<IWorkChangeResultService>(), Substitute.For<IWorkNotificationFacade>(), _dayLockService,
            Substitute.For<IReplacementRequestRecorder>(), _httpContextAccessor, NewGuard(),
            Substitute.For<ILogger<PutCommandHandler>>());

    private PostCommandHandler NewPostHandler()
    {
        var conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        return new(
            _workChangeRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<IPeriodHoursService>(), Substitute.For<IWorkNotificationService>(),
            Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkChangeResultService>(),
            _httpContextAccessor, _dayLockService, conflictChecker, Substitute.For<ISupervisorOverrideAuthorizer>(),
            Substitute.For<IReplacementRequestRecorder>(), NewGuard(), Substitute.For<ILogger<PostCommandHandler>>());
    }

    private DeleteCommandHandler NewDeleteHandler()
        => new(
            _workChangeRepository, _workRepository, _clientVisibilityGuard, new ScheduleMapper(),
            Substitute.For<IPeriodHoursService>(), Substitute.For<IWorkNotificationService>(),
            Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkChangeResultService>(),
            _httpContextAccessor, _dayLockService, Substitute.For<IReplacementRequestRecorder>(), NewGuard(),
            Substitute.For<ILogger<DeleteCommandHandler>>());

    private static Work NewWork(Guid? analyseToken, WorkLockLevel level = WorkLockLevel.None)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            AnalyseToken = analyseToken,
            LockLevel = level
        };

    private static WorkChange NewChange(Guid workId, Guid? analyseToken)
        => new() { Id = Guid.NewGuid(), WorkId = workId, AnalyseToken = analyseToken, Type = WorkChangeType.CorrectionStart };

    private static WorkChangeResource NewResource(Guid id, Guid workId)
        => new() { Id = id, WorkId = workId, Type = WorkChangeType.CorrectionStart };
}
