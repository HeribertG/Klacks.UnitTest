// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The terminal state is what a client reads after it missed the SignalR events, so a late failure
/// path must never overwrite a run that already reported success. That rule rests on the unique index
/// on job_id plus the DbUpdateException catch in StoreAsync, and the EF InMemory provider ignores
/// unique indexes - so the "first wins" tests below run over
/// <see cref="UniqueJobIdEnforcingDataBaseContext"/>, which rejects the duplicate the way PostgreSQL
/// does and derives that decision from the model's own index metadata. Against a plain InMemory context
/// both stores would succeed, two rows would survive, and FirstOrDefaultAsync - which has no ORDER BY -
/// would return the right one by luck. That the real database enforces the index is proven separately by
/// Klacks.IntegrationTest/Infrastructure/JobTerminalStateCachePersistenceTests.cs.
/// </summary>
[TestFixture]
public sealed class JobTerminalStateCacheTests
{
    internal sealed record FakeResult(string Value);

    [Test]
    public async Task FirstTerminalStateWins_CompletedIsNotOverwrittenByFailure()
    {
        var harness = JobTerminalStateCacheTestFactory.CreateWithUniqueJobIdIndex<FakeResult>();
        var jobId = Guid.NewGuid();
        var result = new FakeResult("done");

        await harness.Cache.StoreCompletedAsync(jobId, result);
        await harness.Cache.StoreFailedAsync(jobId, "broadcast blew up afterwards");

        // Exactly one row: the unique index rejected the second insert. Asserting on the status alone
        // would rebuild the very trap this test exists to close.
        (await harness.CountRowsAsync(jobId)).ShouldBe(1);

        // The duplicate must be handled as the expected, benign outcome by the DbUpdateException catch,
        // not by the outer safety net that swallows and logs everything else.
        harness.Errors.ShouldBeEmpty();

        var state = await harness.Cache.TryGetAsync(jobId);
        state.Found.ShouldBeTrue();
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result.ShouldBe(result);
        state.Reason.ShouldBeNull();
    }

    [Test]
    public async Task FirstTerminalStateWins_CancelledIsNotOverwritten()
    {
        var harness = JobTerminalStateCacheTestFactory.CreateWithUniqueJobIdIndex<FakeResult>();
        var jobId = Guid.NewGuid();

        await harness.Cache.StoreCancelledAsync(jobId);
        await harness.Cache.StoreFailedAsync(jobId, "late failure");

        (await harness.CountRowsAsync(jobId)).ShouldBe(1);
        harness.Errors.ShouldBeEmpty();

        var state = await harness.Cache.TryGetAsync(jobId);
        state.Found.ShouldBeTrue();
        state.Status.ShouldBe(WizardJobStatusValues.Cancelled);
        state.Reason.ShouldBeNull();
    }

    [Test]
    public async Task UnknownJob_ReportsUnknown()
    {
        var cache = JobTerminalStateCacheTestFactory.Create<FakeResult>();

        var state = await cache.TryGetAsync(Guid.NewGuid());
        state.Found.ShouldBeFalse();
        state.Status.ShouldBe(WizardJobStatusValues.Unknown);
        state.Result.ShouldBeNull();
        state.Reason.ShouldBeNull();
    }

    [Test]
    public async Task StoreFailed_KeepsTheReason()
    {
        var cache = JobTerminalStateCacheTestFactory.Create<FakeResult>();
        var jobId = Guid.NewGuid();

        await cache.StoreFailedAsync(jobId, "engine exploded");

        var state = await cache.TryGetAsync(jobId);
        state.Found.ShouldBeTrue();
        state.Status.ShouldBe(WizardJobStatusValues.Failed);
        state.Reason.ShouldBe("engine exploded");
    }
}
