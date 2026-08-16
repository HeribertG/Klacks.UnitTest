// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Minimal settable TimeProvider for tests that need to move a clock forward deterministically
/// (e.g. driving EscalationChainService through a multi-step reference case) without pulling in the
/// Microsoft.Extensions.TimeProvider.Testing package, which nothing in this repository references yet.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

public sealed class SettableTimeProvider : TimeProvider
{
    public DateTime Now { get; set; }

    public SettableTimeProvider(DateTime now)
    {
        Now = now;
    }

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}
