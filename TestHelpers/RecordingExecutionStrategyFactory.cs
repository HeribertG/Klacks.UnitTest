// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore.Storage;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Execution strategy factory that hands out a RecordingExecutionStrategy writing into the list the test set in Seen.
/// </summary>
/// <param name="dependencies">EF Core dependencies of the factory</param>
internal sealed class RecordingExecutionStrategyFactory : IExecutionStrategyFactory
{
    private readonly ExecutionStrategyDependencies _dependencies;

    public static List<Exception> Seen { get; } = new();

    public RecordingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
    {
        _dependencies = dependencies;
    }

    public IExecutionStrategy Create() => new RecordingExecutionStrategy(_dependencies, Seen);
}
