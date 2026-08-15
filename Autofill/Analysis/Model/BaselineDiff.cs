// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The comparison of this run against a run that already finished and whose measurement was stored as
/// an artifact — the control run of scenario 6, which is the scenario-5 main run and is deliberately
/// not executed a second time.
/// <para>
/// A baseline that could not be read is <see cref="BaselineDiffMode.NotAvailable"/> with empty lists.
/// That state must never be read as "nothing differs": it means nothing was compared. Every assertion
/// built on this record has to check the mode first.
/// </para>
/// </summary>
/// <param name="BaselineLabel">Name of the baseline run, for example Scenario5a.run1</param>
/// <param name="BaselineSource">Path the baseline was read from, or the paths that were tried in vain</param>
/// <param name="Mode">How much of the baseline the diff could use</param>
/// <param name="AttributionRule">
/// The rule text, written into the artifact so a reader can judge the attribution instead of trusting
/// the count. It is stated once and never widened to reach zero
/// </param>
/// <param name="NightDiffs">Night-shift counts per employee, baseline against this run</param>
/// <param name="HoursDiffs">Planned hours per employee, baseline against this run</param>
/// <param name="Notes">Everything the diff could not decide, in plain words</param>
public sealed record BaselineDiff(
    string BaselineLabel,
    string BaselineSource,
    BaselineDiffMode Mode,
    string AttributionRule,
    IReadOnlyList<BaselineNightDiff> NightDiffs,
    IReadOnlyList<BaselineHoursDiff> HoursDiffs,
    IReadOnlyList<string> Notes)
{
    /// <summary>The state of a run that declares no baseline at all.</summary>
    public static BaselineDiff None { get; } = new(
        string.Empty, string.Empty, BaselineDiffMode.NotAvailable, string.Empty, [], [], []);

    /// <summary>True when a baseline measurement was actually read.</summary>
    public bool IsAvailable => Mode != BaselineDiffMode.NotAvailable;

    /// <summary>Night differences the rule could not attribute — the number the specification asks about.</summary>
    public int UnexplainedNightCount
        => NightDiffs.Count(d => d.AttributedTo == BaselineDiffAttribution.Unexplained);

    /// <summary>Night shifts the absent employees lost in total.</summary>
    public int NightsRemovedByAbsence
        => NightDiffs.Where(d => d.AttributedTo == BaselineDiffAttribution.AbsenceRemoved).Sum(d => -d.Delta);
}
