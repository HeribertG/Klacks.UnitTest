// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for SealOpenOrdersJobBackgroundService: a queued job runs SealOpenOrdersCommand through a
/// scoped IMediator carrying the job's own user id, and the outcome — a result or an unhandled
/// exception — is reported back to that exact user via IAgentTriggerService as one of the two
/// bulk-seal trigger events. Uses a real ServiceCollection (not a mocked IServiceProvider) so
/// CreateScope() resolves the substitutes the same way DI would, following the pattern of
/// GroupGeocodingBackgroundServiceTests. A supervisor's batch above the synchronous limit runs end to end
/// (skill -> queue -> job -> real handler): the job payload carries the scope resolved at request time, the job
/// re-filters the orders as they are when it runs, so an order that landed in a hidden group between the preview
/// and the execution is never sealed, and the run executes under the job user's ExecutionPrincipal.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Handlers.Orders;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Orders;
using Microsoft.Extensions.DependencyInjection;
using Klacks.UnitTest.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Orders;

[TestFixture]
public class SealOpenOrdersJobBackgroundServiceTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private IMediator _mediator = null!;
    private IAgentTriggerService _triggerService = null!;
    private ServiceProvider _serviceProvider = null!;
    private SealOpenOrdersJobBackgroundService _sut = null!;
    private TaskCompletionSource<IAgentTriggerEvent> _notified = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _notified = new TaskCompletionSource<IAgentTriggerEvent>();
        _triggerService.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveDispatchOutcome.Empty)
            .AndDoes(ci => _notified.TrySetResult(ci.Arg<IAgentTriggerEvent>()));

        var services = new ServiceCollection();
        services.AddSingleton(_mediator);
        services.AddSingleton(_triggerService);
        _serviceProvider = services.BuildServiceProvider();

        _sut = new SealOpenOrdersJobBackgroundService(
            _serviceProvider, NullLogger<SealOpenOrdersJobBackgroundService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        _sut.Dispose();
        await _serviceProvider.DisposeAsync();
    }

    private const int AboveSynchronousLimit = 26;

    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private static SealOpenOrdersCommand Command(bool apply = true) =>
        new(SourceSystemId: null, FromDate: null, UntilDate: null, CustomerName: null, GroupId: null,
            MaxCount: null, AutoAssignGroups: false, ValidFrom: null, apply, "tester",
            OpenOrderSealingScope.FullAccess());

    private static Shift SealableOrderInGroup(string name, Guid groupId) => new()
    {
        Id = Guid.NewGuid(),
        Status = ShiftStatus.OriginalOrder,
        Name = name,
        Abbreviation = "FS",
        FromDate = new DateOnly(2099, 2, 1),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1,
        GroupItems = new List<GroupItem> { new() { Id = Guid.NewGuid(), GroupId = groupId } }
    };

    private static void MakeSealable(IShiftRepository shiftRepository, Shift order)
    {
        var plannableShiftId = Guid.NewGuid();
        shiftRepository.Get(order.Id).Returns(order);
        shiftRepository.PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == order.Id))
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
        shiftRepository.GetNoTracking(order.Id)
            .Returns(new Shift { Id = order.Id, Status = ShiftStatus.SealedOrder });
        shiftRepository.GetNoTracking(plannableShiftId)
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
    }

    [Test]
    public async Task SupervisorJob_CarriesTheScope_AndReFiltersOnExecution_SoAnOrderAddedToAHiddenGroupIsNeverSealed()
    {
        var visibleRoot = new Group { Id = Guid.NewGuid(), Name = "Region North" };
        var hiddenRoot = new Group { Id = Guid.NewGuid(), Name = "Region South" };
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(new List<Group> { visibleRoot, hiddenRoot });
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        var shiftRepository = Substitute.For<IShiftRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
        var handler = new SealOpenOrdersCommandHandler(
            shiftRepository,
            new OrderSealingService(shiftRepository, unitOfWork, ShiftVisibilityGuards.AllowAll()),
            _mediator);
        string? principalDuringJob = null;
        _mediator.Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (ci.Arg<SealOpenOrdersCommand>().Apply)
                {
                    principalDuringJob = ExecutionPrincipal.CurrentUserId;
                }

                return handler.Handle(ci.Arg<SealOpenOrdersCommand>(), ci.Arg<CancellationToken>());
            });

        var visibleOrders = Enumerable.Range(1, AboveSynchronousLimit)
            .Select(i => SealableOrderInGroup($"visible {i}", visibleRoot.Id))
            .ToList();
        foreach (var order in visibleOrders)
        {
            MakeSealable(shiftRepository, order);
        }

        shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(visibleOrders.ToList());

        var skill = new SealOpenOrdersSkill(
            new SealOpenOrdersPlanner(
                groupRepository,
                TestGroupScopeGuard.Restricted([visibleRoot.Id], visibleRoot.Name),
                _mediator,
                companyClock),
            _mediator,
            _sut);
        var supervisor = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "supervisor",
            UserPermissions = Permissions.ExpandRoles([Roles.Authorised]),
            ConfirmedActionBinding = SealOpenOrdersBinding.Of(
                OpenOrderSealingSelection.Of(visibleOrders.Select(order => order.Id), []))
        };

        var accepted = await skill.ExecuteAsync(supervisor, new Dictionary<string, object> { ["apply"] = true });
        accepted.Data.ShouldBeOfType<SealOpenOrdersJobAcceptedResult>().PlannedCount.ShouldBe(AboveSynchronousLimit);

        var addedToHiddenGroupAfterPreview = SealableOrderInGroup("added later", hiddenRoot.Id);
        MakeSealable(shiftRepository, addedToHiddenGroupAfterPreview);
        shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(visibleOrders.Append(addedToHiddenGroupAfterPreview).ToList());

        await _sut.StartAsync(CancellationToken.None);
        var raised = await WaitForNotificationAsync();

        var completed = raised.ShouldBeOfType<BulkSealOrdersCompletedTriggerEvent>();
        completed.TargetUserId.ShouldBe(supervisor.UserId);
        completed.TotalOrders.ShouldBe(AboveSynchronousLimit);
        completed.SealedCount.ShouldBe(AboveSynchronousLimit);
        await shiftRepository.DidNotReceive().PutWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.Id == addedToHiddenGroupAfterPreview.Id));
        principalDuringJob.ShouldBe(supervisor.UserId.ToString());
    }

    [Test]
    public async Task RunsTheJobWithTheGivenUserId_AndPostsACompletedNotificationCarryingTheResultCounts()
    {
        var userId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var command = Command();
        var result = new SealOpenOrdersResult(
            Applied: true, TotalOrders: 40, SealableCount: 37, SealedCount: 35, BlockedCount: 3,
            FailedCount: 2, BlockedOnlyByMissingGroupCount: 0, AutoAssignedCount: 0,
            AutoAssignRequested: false, SealedSample: [],
            BlockedSample: [],
            Failures: [new FailedOrder(Guid.NewGuid(), "bad order", "db timeout")]);
        _mediator.Send(Arg.Is<SealOpenOrdersCommand>(c => c == command), Arg.Any<CancellationToken>())
            .Returns(result);

        await _sut.StartAsync(CancellationToken.None);
        _sut.Enqueue(new SealOpenOrdersJob(jobId, userId, command));
        var raised = await WaitForNotificationAsync();

        await _mediator.Received(1).Send(Arg.Is<SealOpenOrdersCommand>(c => c == command), Arg.Any<CancellationToken>());
        var completed = raised.ShouldBeOfType<BulkSealOrdersCompletedTriggerEvent>();
        completed.JobId.ShouldBe(jobId);
        completed.TargetUserId.ShouldBe(userId);
        completed.SealedCount.ShouldBe(35);
        completed.BlockedCount.ShouldBe(3);
        completed.FailedCount.ShouldBe(2);
        completed.TotalOrders.ShouldBe(40);
        completed.FailureSample.ShouldContain(("bad order", "db timeout"));
    }

    [Test]
    public async Task JobThrows_PostsAFailedNotification_InsteadOfCrashingTheLoop()
    {
        var userId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var command = Command();
        _mediator.Send(Arg.Is<SealOpenOrdersCommand>(c => c == command), Arg.Any<CancellationToken>())
            .Returns<SealOpenOrdersResult>(_ => throw new InvalidOperationException("database exploded"));

        await _sut.StartAsync(CancellationToken.None);
        _sut.Enqueue(new SealOpenOrdersJob(jobId, userId, command));
        var raised = await WaitForNotificationAsync();

        var failed = raised.ShouldBeOfType<BulkSealOrdersFailedTriggerEvent>();
        failed.JobId.ShouldBe(jobId);
        failed.TargetUserId.ShouldBe(userId);
        failed.ErrorMessage.ShouldContain("database exploded");
    }

    private async Task<IAgentTriggerEvent> WaitForNotificationAsync()
    {
        var finished = await Task.WhenAny(_notified.Task, Task.Delay(CompletionTimeout));
        finished.ShouldBe(_notified.Task, "the trigger service was not invoked within the timeout");
        return await _notified.Task;
    }
}
