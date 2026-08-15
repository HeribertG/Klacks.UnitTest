// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// The outcome of looking for a stored baseline measurement: either the snapshot, or the reason there
/// is none. The two are kept in one object on purpose — a caller that only received a nullable
/// snapshot would have nothing to print when it is null, and "no baseline" would become "no
/// difference".
/// </summary>
/// <param name="Snapshot">The baseline measurement, or null when none could be read</param>
/// <param name="Source">The file that was read, or the paths that were tried in vain</param>
/// <param name="Problem">Why there is no snapshot; null when there is one</param>
public sealed record BaselineMetricsLoad(
    BaselineMetricsSnapshot? Snapshot,
    string Source,
    string? Problem)
{
    /// <summary>True when a baseline measurement was actually read.</summary>
    public bool IsAvailable => Snapshot is not null;
}
