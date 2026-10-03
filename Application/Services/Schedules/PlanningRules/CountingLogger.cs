// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules.PlanningRules;

/// <summary>Test logger that only counts the entries logged at Error level.</summary>
internal sealed class CountingLogger<T> : ILogger<T>
{
    public int ErrorCount { get; private set; }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Error)
        {
            ErrorCount++;
        }
    }
}
