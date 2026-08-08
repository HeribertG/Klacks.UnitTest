// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Diagnosis artifact of one scenario: what the current engine produced on each of the baseline seeds,
/// the two ends of the band that spans, and the values the scenario has pinned today. Nothing here is
/// asserted — it exists so a later analysis can see the band a guard was pinned against and re-pin from
/// <see cref="Worst"/> without having to guess which seed produced which number.
/// </summary>
/// <param name="Scenario">Scenario name</param>
/// <param name="TestName">Test name; the artifact is named after it</param>
/// <param name="MeasuredAtUtc">When this report was written</param>
/// <param name="Note">How to read the report</param>
/// <param name="Samples">One entry per seed, in seed order</param>
/// <param name="Worst">Metric by metric the worst of the samples — the values the guards pin</param>
/// <param name="Best">Metric by metric the best of the samples — the other end of the band</param>
/// <param name="CurrentlyPinned">What the scenario's baseline constants hold at the time of the run</param>
public sealed record AutofillBandReport(
    string Scenario,
    string TestName,
    DateTime MeasuredAtUtc,
    string Note,
    IReadOnlyList<AutofillBandSample> Samples,
    AutofillBandValues Worst,
    AutofillBandValues Best,
    AutofillBandValues CurrentlyPinned);
