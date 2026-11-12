// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// The compact comparison of one scenario: one line per quantity, one column per stage (W1 | W2 | W3). The production
/// chain is W1 -> W3; W2 is measured on the same W1 plan as the single stage it still is.
/// Every value is read from the analyzer's measurement of the stage or from <see cref="ChainPlanFacts"/>; nothing here
/// judges a value.
/// </summary>
public static class ChainSummaryTable
{
    private const int LabelWidth = 46;
    private const int ValueWidth = 16;
    private const string ColumnSeparator = " | ";
    private const string ShareFormat = "0.###";
    private const string HoursFormat = "0.#";
    private const string PercentFormat = "0.0";
    private const string FitnessFormat = "0.0000";
    private const char RuleCharacter = '-';
    private const double PercentPerRatio = 100;

    /// <param name="chainName">Artifact name of the scenario</param>
    /// <param name="definition">Scenario the chain ran on</param>
    /// <param name="stages">Measurements of W1, W2 and W3, then any comparison runs</param>
    /// <param name="judgedRankCount">Highest list rank the shift-kind spread is taken over</param>
    /// <param name="judgedKindsNote">Which shift kinds the scenario's specification judges for the spread</param>
    /// <param name="run">Outcome of Wizard 2 and 3, for the stop reason and the hint count</param>
    public static string Render(
        string chainName,
        AutofillScenarioDefinition definition,
        IReadOnlyList<ChainStageMeasurement> stages,
        int judgedRankCount,
        string judgedKindsNote,
        ChainRunResult run)
    {
        var text = new StringBuilder();
        var search = run.Wizard3Search;
        text.AppendLine($"{chainName}: autofill chain W1 -> W3 (W2 measured alone on the W1 plan), period {definition.PeriodFrom:yyyy-MM-dd}..{definition.PeriodUntil:yyyy-MM-dd}");
        text.AppendLine(Invariant($"W2 softening hints {run.SofteningHintCount}; W3 stop {search.StopReason}, harmony fitness {search.FitnessBefore.ToString(FitnessFormat, CultureInfo.InvariantCulture)} -> {search.FitnessAfter.ToString(FitnessFormat, CultureInfo.InvariantCulture)}, applied batches {search.AppliedBatches.Count}, evaluations {search.Evaluations}"));
        text.AppendLine("The engine fitness in the w2/w3 metrics files is 0: the analyzer reads Wizard 1's fitness fields, which the bitmap stages do not produce.");
        text.AppendLine($"Shift-kind spread over ranks 1-{judgedRankCount}; {judgedKindsNote}");
        AppendRow(text, "quantity", stages.Select(s => s.Label.ToUpperInvariant()));
        text.AppendLine(new string(RuleCharacter, LabelWidth + (stages.Count * (ValueWidth + ColumnSeparator.Length))));

        AppendRow(text, $"unfilled shifts (of {stages[0].Metrics.Coverage.TotalRequiredShifts})", stages.Select(s => Count(s.Metrics.Coverage.UnfilledShifts.Count)));
        AppendRow(text, "double bookings", stages.Select(s => Count(s.Metrics.Coverage.DoubleBookings.Count)));
        AppendRow(text, "rest / night-to-early violations", stages.Select(s => $"{Count(s.Metrics.Legality.RestViolations.Count)} / {Count(s.Metrics.Legality.NightToEarlyViolations.Count)}"));
        AppendRow(text, "mixed-kind packages / packages", stages.Select(s => $"{Count(s.Metrics.Packages.MixedTypeCount)} / {Count(s.Metrics.Packages.Items.Count)}"));
        AppendRow(text, $"share of packages <= {AutofillSpecConstants.ShortPackageMaxLength} days", stages.Select(s => Share(AutofillPlanAnalyzer.ShortPackageShare(s.Metrics.Packages, AutofillSpecConstants.ShortPackageMaxLength))));
        AppendRow(text, $"packages > {AutofillSpecConstants.MaxAllowedPackageLength} days", stages.Select(s => Count(AutofillPlanAnalyzer.PackagesLongerThan(s.Metrics.Packages, AutofillSpecConstants.MaxAllowedPackageLength))));
        AppendRow(text, "ideal block transitions (rate, ideal/all)", stages.Select(s => $"{Share(s.Metrics.Rotation.Spec.IdealTransitionRate)} ({Count(s.Metrics.Rotation.Spec.IdealTransitionCount)}/{Count(s.Metrics.Rotation.Spec.TransitionCount)})"));
        AppendRow(text, "unforced rotation deviations", stages.Select(s => Count(s.Metrics.Rotation.Spec.UnforcedDeviations)));
        AppendRow(text, $"kind spread early/late/night, ranks 1-{judgedRankCount}", stages.Select(s => SpreadOf(s, judgedRankCount)));

        foreach (var employee in stages[0].Metrics.Hours.PerEmployee)
        {
            AppendRow(text, $"planned h / fulfilment, rank {employee.ListRank} {employee.Employee}", stages.Select(s => HoursOf(s, employee.Employee)));
        }

        AppendRow(text, "rank monotonicity violations", stages.Select(s => Count(s.Metrics.Hours.MonotonicityViolations.Count)));
        AppendRow(text, "changed assignments vs W1", stages.Select(s => Count(s.ChangedVsWizard1)));

        var weekly = stages.Select(s => ChainPlanFacts.HighestWeeklyHours(s.Plan, definition)).ToList();
        foreach (var employee in definition.EmployeesInListOrder)
        {
            AppendRow(text, $"highest ISO-week hours {employee}", weekly.Select(w => Hours(w.Single(e => e.Employee == employee).Hours)));
        }

        AppendRow(text, $"package pairs with < {RotationSpecAnalyzer.BlockBoundaryRestHours} h rest", stages.Select(s => Count(ChainPlanFacts.ShortRestPackagePairs(s.Plan, definition))));

        var weekend = stages.Select(s => ChainPlanFacts.WeekendDays(s.Plan, definition)).ToList();
        foreach (var employee in definition.EmployeesInListOrder)
        {
            AppendRow(text, $"weekend days {employee}", weekend.Select(w => Count(w.Single(e => e.Employee == employee).Days)));
        }

        AppendRow(text, "weekend days spread (max - min)", weekend.Select(w => Count(w.Max(e => e.Days) - w.Min(e => e.Days))));

        var staircase = stages.Select(s => StaircaseHarmonyAnalyzer.Analyze(s.Plan, definition)).ToList();
        AppendRow(text, "staircase: rows as shifted copies (chance)", staircase.Select(h => $"{Share(h.CycleSimilarity)} ({Share(h.ChanceSimilarity)})"));
        AppendRow(text, "staircase: regular step, share (step days)", staircase.Select(h => $"{Share(h.StepRegularity)} ({Count(h.ModalStepDays)})"));
        return text.ToString();
    }

    private static string SpreadOf(ChainStageMeasurement stage, int judgedRankCount)
    {
        var spread = AutofillPlanAnalyzer.SpreadOf(
            stage.Metrics.Fairness.ShiftTypeCountPerEmployee.Where(c => c.ListRank <= judgedRankCount));
        return $"{Count(spread.Early)}/{Count(spread.Late)}/{Count(spread.Night)}";
    }

    private static string HoursOf(ChainStageMeasurement stage, string employee)
    {
        var hours = stage.Metrics.Hours.PerEmployee.Single(e => e.Employee == employee);
        return $"{Hours(hours.PlannedHours)} h {(hours.FulfillmentPct * PercentPerRatio).ToString(PercentFormat, CultureInfo.InvariantCulture)} %";
    }

    private static void AppendRow(StringBuilder text, string label, IEnumerable<string> values)
    {
        text.Append(label.PadRight(LabelWidth));
        foreach (var value in values)
        {
            text.Append(ColumnSeparator).Append(value.PadLeft(ValueWidth));
        }

        text.AppendLine();
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Share(double value) => value.ToString(ShareFormat, CultureInfo.InvariantCulture);

    private static string Hours(double value) => value.ToString(HoursFormat, CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
