// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * Minimal ILogger that forwards Information+ lines of the engine to the NUnit progress stream, so a benchmark
 * run can explain why the inner loop stopped. Enabled only when KLACKS_BENCH_ENGINE_LOG is set.
 */

using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

public sealed class Stage3ProgressLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            TestContext.Progress.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
