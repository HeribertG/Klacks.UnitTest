// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// In-memory ILogger that records each entry's level, rendered message and exception so tests
/// can assert on logging behaviour such as level counts and stack-trace suppression.
/// </summary>
/// <typeparam name="T">Category type of the logger</typeparam>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<RecordedLogEntry> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new RecordedLogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
