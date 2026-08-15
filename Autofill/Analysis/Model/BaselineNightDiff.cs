// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One employee's night-shift count in the baseline run next to the same count in this run, with the
/// cause the attribution rule assigns to the difference.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ListRank">Position in the displayed list, 1 = top</param>
/// <param name="BaselineNights">Night shifts the baseline run gave him</param>
/// <param name="CurrentNights">Night shifts this run gives him</param>
/// <param name="AttributedTo">Cause the attribution rule assigns</param>
/// <param name="Explanation">The reasoning in plain words, so the count can be judged and not only read</param>
public sealed record BaselineNightDiff(
    string Employee,
    int ListRank,
    int BaselineNights,
    int CurrentNights,
    BaselineDiffAttribution AttributedTo,
    string Explanation)
{
    /// <summary>Current minus baseline; negative when the employee lost night shifts.</summary>
    public int Delta => CurrentNights - BaselineNights;
}
