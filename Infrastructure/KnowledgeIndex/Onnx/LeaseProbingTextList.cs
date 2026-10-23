// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

/// <summary>
/// Text list for EmbedBatchAsync that runs a probe on every access once the batch has started reading
/// its texts, so a test can ask "is the session still held?" from inside the chunk loop itself instead
/// of racing it from another thread.
/// </summary>
/// <param name="texts">The texts handed to the batch.</param>
/// <param name="probeLeaseHeld">Runs synchronously on the batch's own thread; returns whether the
/// session was still held at that point.</param>
public sealed class LeaseProbingTextList(IReadOnlyList<string> texts, Func<bool> probeLeaseHeld)
    : IReadOnlyList<string>
{
    private bool _armed;

    public List<bool> ProbeResults { get; } = [];

    public int Count
    {
        get
        {
            ProbeIfArmed();
            return texts.Count;
        }
    }

    public string this[int index]
    {
        get
        {
            _armed = true;
            ProbeIfArmed();
            return texts[index];
        }
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var i = 0; i < texts.Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void ProbeIfArmed()
    {
        if (_armed)
        {
            ProbeResults.Add(probeLeaseHeld());
        }
    }
}
