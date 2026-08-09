// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Wall-clock cost of one engine run. Measured by the runner rather than by the analyzer, which never
/// sees a clock; a scenario that was not timed reports zero.
/// </summary>
/// <param name="WallClockMs">Milliseconds one TokenEvolutionLoop.Run of this scenario took</param>
/// <param name="RatioVsScenario2">
/// <paramref name="WallClockMs"/> divided by the reference run of scenario 2, measured in the same
/// session on the same machine; 0 when no reference was supplied
/// </param>
public sealed record RuntimeMetrics(long WallClockMs, double RatioVsScenario2)
{
    /// <summary>The measurement of a run nobody timed.</summary>
    public static RuntimeMetrics NotMeasured { get; } = new(0, 0);
}
