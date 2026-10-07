// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Synchronous progress sink for one engine run: remembers the last generation the loop reported and
/// whether it stopped on the no-improvement plateau. <see cref="Progress{T}"/> is deliberately not used,
/// because it posts its callbacks asynchronously and the values would not be settled when the run returns.
/// </summary>
public sealed class GenerationRecorder : IProgress<TokenEvolutionProgress>
{
    /// <summary>Last generation the loop reported; 0 when it reported none.</summary>
    public int LastGeneration { get; private set; }

    /// <summary>True when the last report announced the early stop.</summary>
    public bool StoppedEarly { get; private set; }

    /// <inheritdoc />
    public void Report(TokenEvolutionProgress value)
    {
        LastGeneration = value.Generation;
        StoppedEarly = value.EarlyStopping;
    }
}
