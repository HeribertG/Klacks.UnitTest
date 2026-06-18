// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Services;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class HeavyWorkGateTests
{
    [Test]
    public void TryAcquire_AllowsOneHolder_BlocksASecond_AndFreesAfterDispose()
    {
        var gate = new HeavyWorkGate();

        gate.TryAcquire(out var first).ShouldBeTrue();
        first.ShouldNotBeNull();

        gate.TryAcquire(out var second).ShouldBeFalse();
        second.ShouldBeNull();

        first!.Dispose();

        gate.TryAcquire(out var third).ShouldBeTrue();
        third!.Dispose();
    }

    [Test]
    public void Dispose_IsIdempotent()
    {
        var gate = new HeavyWorkGate();
        gate.TryAcquire(out var lease).ShouldBeTrue();

        lease!.Dispose();
        lease.Dispose(); // must not over-release

        // The gate is free for exactly one holder; a double-release would have allowed two.
        gate.TryAcquire(out var a).ShouldBeTrue();
        gate.TryAcquire(out var b).ShouldBeFalse();
        a!.Dispose();
        b.ShouldBeNull();
    }

    [Test]
    public async Task AcquireAsync_WaitsUntilTheHolderReleases()
    {
        var gate = new HeavyWorkGate();
        var held = await gate.AcquireAsync(CancellationToken.None);

        var second = gate.AcquireAsync(CancellationToken.None);
        second.IsCompleted.ShouldBeFalse(); // blocked while held

        held.Dispose();
        var secondLease = await second; // now proceeds
        secondLease.Dispose();
    }
}
