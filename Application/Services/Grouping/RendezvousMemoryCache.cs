// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Test cache that makes a lookup wait (up to a timeout) until a second caller has also started a
/// lookup, so two unsynchronised read-then-write sequences reliably interleave, and counts the misses.
/// A caller that serialises its read-then-write under a lock lets the first lookup time out alone, and
/// the second caller then sees the first caller's entry.
/// </summary>
/// <param name="inner">Real cache that stores the entries.</param>
/// <param name="participants">Number of lookups that meet before any of them continues.</param>
/// <param name="timeout">Longest wait of one lookup for the others.</param>

using Microsoft.Extensions.Caching.Memory;

namespace Klacks.UnitTest.Application.Services.Grouping;

public sealed class RendezvousMemoryCache : IMemoryCache
{
    private readonly IMemoryCache _inner;
    private readonly CountdownEvent _rendezvous;
    private readonly TimeSpan _timeout;
    private int _misses;

    public RendezvousMemoryCache(IMemoryCache inner, int participants, TimeSpan timeout)
    {
        _inner = inner;
        _rendezvous = new CountdownEvent(participants);
        _timeout = timeout;
    }

    public int Misses => Volatile.Read(ref _misses);

    public bool TryGetValue(object key, out object? value)
    {
        var found = _inner.TryGetValue(key, out value);
        if (!found)
        {
            Interlocked.Increment(ref _misses);
        }

        if (!_rendezvous.IsSet)
        {
            _rendezvous.Signal();
        }

        _rendezvous.Wait(_timeout);
        return found;
    }

    public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);

    public void Remove(object key) => _inner.Remove(key);

    public void Dispose()
    {
        _rendezvous.Dispose();
        _inner.Dispose();
    }
}
