// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Counts diagnosis outcomes of the swap-chain probe by label and keeps the first example seen for each label, so a
/// report can show how often a rule blocked and one concrete constellation behind the number.
/// </summary>
public sealed class SwapChainProbeTally
{
    private const string NothingCounted = "none";

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _examples = new(StringComparer.Ordinal);

    /// <summary>Counts one outcome.</summary>
    /// <param name="label">Outcome, for example the stage and the rule that refused it</param>
    /// <param name="example">Constellation behind the outcome; only the first one per label is kept</param>
    public void Add(string label, string example)
    {
        _counts[label] = _counts.GetValueOrDefault(label) + 1;
        _examples.TryAdd(label, example);
    }

    /// <summary>The counts as one text, most frequent first.</summary>
    public string Counts()
        => _counts.Count == 0
            ? NothingCounted
            : string.Join(
                ", ",
                Ordered().Select(entry => entry.Key + " x" + entry.Value.ToString(CultureInfo.InvariantCulture)));

    /// <summary>One text per label: the label and its first example, most frequent label first.</summary>
    public IEnumerable<string> Examples()
        => Ordered().Select(entry => entry.Key + ": " + _examples[entry.Key]);

    private IEnumerable<KeyValuePair<string, int>> Ordered()
        => _counts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal);
}
