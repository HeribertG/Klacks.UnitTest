// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore.Storage;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Execution strategy that records every exception it is asked to classify and never retries, so a test can see what
/// the retrying strategy of production would be shown.
/// </summary>
/// <param name="dependencies">EF Core dependencies of the strategy</param>
/// <param name="seen">Receives each exception passed to ShouldRetryOn</param>
internal sealed class RecordingExecutionStrategy : ExecutionStrategy
{
    private const int NoRetries = 0;
    private readonly List<Exception> _seen;

    public RecordingExecutionStrategy(ExecutionStrategyDependencies dependencies, List<Exception> seen)
        : base(dependencies, NoRetries, TimeSpan.Zero)
    {
        _seen = seen;
    }

    protected override bool ShouldRetryOn(Exception exception)
    {
        _seen.Add(exception);
        return false;
    }
}
