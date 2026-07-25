// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GroupGeocodingBackgroundService: on startup it resumes every group that was queued but
/// never actually resolved (e.g. lost by a restart while the in-memory channel still had items) by
/// reading them from IGroupRepository and running them through the same resolver as a manual Queue()
/// call — a real restart-resilience regression would show up as the resolver never being invoked.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class GroupGeocodingBackgroundServiceTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private IGroupRepository _groupRepository = null!;
    private IGroupLocationResolver _resolver = null!;
    private ServiceProvider _serviceProvider = null!;
    private GroupGeocodingBackgroundService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.GetUnattemptedGeocodingCandidateIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());

        _resolver = Substitute.For<IGroupLocationResolver>();

        var services = new ServiceCollection();
        services.AddSingleton(_groupRepository);
        services.AddSingleton(_resolver);
        _serviceProvider = services.BuildServiceProvider();

        _sut = new GroupGeocodingBackgroundService(
            _serviceProvider, NullLogger<GroupGeocodingBackgroundService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        _sut.Dispose();
        await _serviceProvider.DisposeAsync();
    }

    [Test]
    public async Task Start_LeftoverGroupsFromPreviousRun_AreResolvedWithoutManualQueue()
    {
        var lostGroupId = Guid.NewGuid();
        _groupRepository.GetUnattemptedGeocodingCandidateIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { lostGroupId });
        SignalOnResolve(lostGroupId);

        await StartAndWaitForResolveAsync();

        await _resolver.Received(1).ResolveAsync(lostGroupId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_NoLeftoverGroups_QueueStillAcceptsManualEnqueue()
    {
        var manuallyQueuedId = Guid.NewGuid();
        SignalOnResolve(manuallyQueuedId);

        await _sut.StartAsync(CancellationToken.None);
        _sut.Queue(manuallyQueuedId);
        await WaitForSignalAsync();

        await _resolver.Received(1).ResolveAsync(manuallyQueuedId, Arg.Any<CancellationToken>());
    }

    private TaskCompletionSource _resolved = null!;

    private void SignalOnResolve(Guid groupId)
    {
        _resolved = new TaskCompletionSource();
        _resolver.ResolveAsync(groupId, Arg.Any<CancellationToken>())
            .Returns(new GroupLocationResolveResult(groupId, "Test", GroupLocationResolveOutcome.NotAPlace, null, null))
            .AndDoes(_ => _resolved.TrySetResult());
    }

    private async Task StartAndWaitForResolveAsync()
    {
        await _sut.StartAsync(CancellationToken.None);
        await WaitForSignalAsync();
    }

    private async Task WaitForSignalAsync()
    {
        var finished = await Task.WhenAny(_resolved.Task, Task.Delay(CompletionTimeout));
        finished.ShouldBe(_resolved.Task, "the resolver was not invoked within the timeout");
    }
}
