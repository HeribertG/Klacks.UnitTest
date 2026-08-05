// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The terminal state is what a client reads after it missed the SignalR events, so a late failure
/// path must never overwrite a run that already reported success.
/// </summary>
[TestFixture]
public sealed class JobTerminalStateCacheTests
{
    private sealed record FakeResult(string Value);

    [Test]
    public void FirstTerminalStateWins_CompletedIsNotOverwrittenByFailure()
    {
        var cache = new JobTerminalStateCache<FakeResult>();
        var jobId = Guid.NewGuid();
        var result = new FakeResult("done");

        cache.StoreCompleted(jobId, result);
        cache.StoreFailed(jobId, "broadcast blew up afterwards");

        cache.TryGet(jobId, out var status, out var cached, out var reason).ShouldBeTrue();
        status.ShouldBe(WizardJobStatusValues.Completed);
        cached.ShouldBe(result);
        reason.ShouldBeNull();
    }

    [Test]
    public void FirstTerminalStateWins_CancelledIsNotOverwritten()
    {
        var cache = new JobTerminalStateCache<FakeResult>();
        var jobId = Guid.NewGuid();

        cache.StoreCancelled(jobId);
        cache.StoreFailed(jobId, "late failure");

        cache.TryGet(jobId, out var status, out _, out _).ShouldBeTrue();
        status.ShouldBe(WizardJobStatusValues.Cancelled);
    }

    [Test]
    public void UnknownJob_ReportsUnknown()
    {
        var cache = new JobTerminalStateCache<FakeResult>();

        cache.TryGet(Guid.NewGuid(), out var status, out var cached, out var reason).ShouldBeFalse();
        status.ShouldBe(WizardJobStatusValues.Unknown);
        cached.ShouldBeNull();
        reason.ShouldBeNull();
    }

    [Test]
    public void StoreFailed_KeepsTheReason()
    {
        var cache = new JobTerminalStateCache<FakeResult>();
        var jobId = Guid.NewGuid();

        cache.StoreFailed(jobId, "engine exploded");

        cache.TryGet(jobId, out var status, out _, out var reason).ShouldBeTrue();
        status.ShouldBe(WizardJobStatusValues.Failed);
        reason.ShouldBe("engine exploded");
    }
}
