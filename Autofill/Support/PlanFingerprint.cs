// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Canonical text form of a plan, used to compare two runs deeply instead of comparing scores.
/// <para>
/// Block ids and the scenario id are deliberately left out: every token gets a fresh Guid for them
/// on creation, they serve only as grouping keys and take part in no ordering, so including them
/// would report two identical plans as different. Everything that decides WHO works WHEN is in.
/// </para>
/// </summary>
public static class PlanFingerprint
{
    private const string FieldSeparator = "|";

    /// <summary>Fingerprint of a plan: one ordinally sorted line per assignment.</summary>
    /// <param name="scenario">Plan to fingerprint</param>
    public static string ForScenario(CoreScenario scenario)
    {
        var lines = scenario.Tokens
            .Select(t => string.Join(
                FieldSeparator,
                t.AgentId,
                t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                t.ShiftRefId.ToString(),
                t.StartAt.ToString("O", CultureInfo.InvariantCulture),
                t.EndAt.ToString("O", CultureInfo.InvariantCulture),
                t.TotalHours.ToString(CultureInfo.InvariantCulture),
                t.ShiftTypeIndex.ToString(CultureInfo.InvariantCulture),
                t.IsLocked ? "locked" : "planned"))
            .OrderBy(line => line, StringComparer.Ordinal);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Fingerprint of the fixed previous-month works, to prove the run never touched them.</summary>
    /// <param name="works">Boundary works handed to the engine</param>
    public static string ForBoundaryWorks(IReadOnlyList<CoreLockedWork> works)
    {
        var lines = works
            .Select(w => string.Join(
                FieldSeparator,
                w.AgentId,
                w.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                w.ShiftTypeIndex.ToString(CultureInfo.InvariantCulture),
                w.StartAt.ToString("O", CultureInfo.InvariantCulture),
                w.EndAt.ToString("O", CultureInfo.InvariantCulture),
                w.TotalHours.ToString(CultureInfo.InvariantCulture),
                w.ShiftRefId.ToString(),
                w.WorkId))
            .OrderBy(line => line, StringComparer.Ordinal);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Describes the first line in which two fingerprints differ, or null when they are equal.
    /// </summary>
    /// <param name="left">First fingerprint</param>
    /// <param name="right">Second fingerprint</param>
    public static string? FirstDifference(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return null;
        }

        var leftLines = left.Split(Environment.NewLine);
        var rightLines = right.Split(Environment.NewLine);
        var shared = Math.Min(leftLines.Length, rightLines.Length);

        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(leftLines[i], rightLines[i], StringComparison.Ordinal))
            {
                return new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"line {i + 1}: '")
                    .Append(leftLines[i])
                    .Append("' vs '")
                    .Append(rightLines[i])
                    .Append('\'')
                    .ToString();
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"different number of assignments: {leftLines.Length} vs {rightLines.Length}");
    }
}
