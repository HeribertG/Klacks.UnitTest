// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Thread-safe variant of RecordingLogger for code that logs from pool threads (progress callbacks, continuations)
/// while its main flow logs too. Entries returns a snapshot in arrival order.
/// </summary>
/// <typeparam name="T">Category type of the logger</typeparam>
public sealed class ConcurrentRecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<RecordedLogEntry> _entries = new();

    public List<RecordedLogEntry> Entries => _entries.ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue(new RecordedLogEntry(logLevel, formatter(state, exception), exception));
}
