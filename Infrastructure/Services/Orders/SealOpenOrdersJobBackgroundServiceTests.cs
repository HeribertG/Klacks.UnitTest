// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SealOpenOrdersJobBackgroundService: a queued job runs SealOpenOrdersCommand through a
/// scoped IMediator carrying the job's own user id, and the outcome — a result or an unhandled
/// exception — is reported back to that exact user via IAgentTriggerService as one of the two
/// bulk-seal trigger events. Uses a real ServiceCollection (not a mocked IServiceProvider) so
/// CreateScope() resolves the substitutes the same way DI would, following the pattern of
/// GroupGeocodingBackgroundServiceTests.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Orders;
using Microsoft.Extensions.DependencyInjection;
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
            .Returns(Task.CompletedTask)
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

    private static SealOpenOrdersCommand Command(bool apply = true) =>
        new(SourceSystemId: null, FromDate: null, UntilDate: null, CustomerName: null, GroupId: null,
            MaxCount: null, AutoAssignGroups: false, ValidFrom: null, apply, "tester");

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
