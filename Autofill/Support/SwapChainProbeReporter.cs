// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Recovery;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Runs the swap-chain measurement over the finished plan of a scenario run and writes the numbers
/// next to the run's diagnosis: closed slots per chain length for every rule setting, and what the
/// chains cost in package quality, the refusals by rule for every slot that stays open
/// (<see cref="SwapChainProbeOpenSlotDiagnosis"/>) and the positive control with its refusals
/// (<see cref="SwapChainProbeSelfCheck"/>); the report also holds the counter-check with the recovery engine
/// (<see cref="RecoveryEngineGapProbe"/>). It never asserts and never fails a test — a measurement that
/// cannot be taken is reported as such.
/// </summary>
public static class SwapChainProbeReporter
{
    private const string Prefix = "swap-chain probe ";
    private const string RunLabel = "swap-chain-probe";
    private const string ReportExtension = ".swap-chain-probe.txt";
    private const string SectionOpen = " section [";
    private const string SectionFailed = "] failed: ";
    private const string SectionReportFile = "report file";
    private const string SectionBefore = "before";
    private const string SectionMeasurement = "measurement ";
    private const string SectionDiagnosis = "open-slot diagnosis ";
    private const string SectionSelfCheck = "self-check";
    private const string SectionRecoveryEngine = "recovery-engine probe";
    private const string SectionAfter = "after analysis ";
    private const string SectionChains = "chain list ";

    /// <summary>
    /// Measures every rule setting and writes the report lines to the test output and to a text file
    /// next to the run's artifacts; the file is the reliable copy, because output written while a
    /// fixture is set up does not reach the result file of the test run. Every section runs on its own: one that
    /// fails is reported as a single line and the others still run. The quality lines after the chains come last.
    /// </summary>
    /// <param name="scenarioName">Scenario name of the run</param>
    /// <param name="artifactName">Artifact name of the run</param>
    /// <param name="definition">Scenario that was run</param>
    /// <param name="run">Result of the deterministic runner</param>
    public static void Write(
        string scenarioName, string artifactName, AutofillScenarioDefinition definition, DeterministicRunResult run)
    {
        string? reportPath = null;
        RunSection(reportPath, artifactName, SectionReportFile, () =>
        {
            reportPath = Path.Combine(
                AutofillRepositoryPaths.ArtifactDirectory(run.Metrics.Scenario), artifactName + ReportExtension);
            File.WriteAllText(reportPath, string.Empty);
        });

        RunSection(
            reportPath,
            artifactName,
            SectionBefore,
            () => Emit(reportPath, Prefix + artifactName + " before: " + Quality(run.Metrics)));

        var results = new List<SwapChainProbeResult>();
        foreach (var variant in SwapChainProbeVariant.All)
        {
            RunSection(reportPath, artifactName, SectionMeasurement + variant.Label, () =>
            {
                var watch = Stopwatch.StartNew();
                var result = SwapChainProbe.Measure(run.Plan, definition.Context, variant);
                results.Add(result);
                Emit(
                    reportPath,
                    Prefix + artifactName + " [" + variant.Label + "]: " + Counts(result) + "; search took "
                    + watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            });
        }

        foreach (var variant in SwapChainProbeVariant.Diagnosed)
        {
            RunSection(reportPath, artifactName, SectionDiagnosis + variant.Label, () =>
            {
                foreach (var line in SwapChainProbeOpenSlotDiagnosis.Describe(run.Plan, definition.Context, variant))
                {
                    Emit(reportPath, artifactName + ": " + line);
                }
            });
        }

        RunSection(reportPath, artifactName, SectionSelfCheck, () =>
        {
            foreach (var line in SwapChainProbeSelfCheck.Run(run.Plan, definition.Context))
            {
                Emit(reportPath, artifactName + ": " + line);
            }
        });

        RunSection(reportPath, artifactName, SectionRecoveryEngine, () =>
        {
            foreach (var line in RecoveryEngineGapProbe.Describe(run.Plan, definition.Context))
            {
                Emit(reportPath, artifactName + ": " + line);
            }
        });

        foreach (var result in results)
        {
            if (result.Chains.Count == 0)
            {
                continue;
            }

            var label = result.Variant.Label;
            RunSection(reportPath, artifactName, SectionAfter + label, () =>
            {
                var after = AutofillPlanAnalyzer.Analyze(result.Plan, definition, scenarioName, artifactName, RunLabel);
                Emit(reportPath, Prefix + artifactName + " [" + label + "] after: " + Quality(after));
            });
            RunSection(reportPath, artifactName, SectionChains + label, () =>
            {
                for (var index = 0; index < result.Chains.Count; index++)
                {
                    Emit(
                        reportPath,
                        Prefix + artifactName + " [" + label + "] chain: " + result.Chains[index]
                        + (result.Variant.PackageRestMayShorten
                            ? " (package-rest exceptions "
                                + result.PackageRestExceptions[index].ToString(CultureInfo.InvariantCulture) + ")"
                            : string.Empty));
                }
            });
        }
    }

    private static void RunSection(string? reportPath, string artifactName, string section, Action body)
    {
        try
        {
            body();
        }
        catch (Exception exception)
        {
            Emit(
                reportPath,
                Prefix + artifactName + SectionOpen + section + SectionFailed + exception.GetType().Name + " "
                + exception.Message.ReplaceLineEndings(" "));
        }
    }

    private static void Emit(string? reportPath, string line)
    {
        TestContext.Out.WriteLine(line);
        if (reportPath is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(reportPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }

    private static string Counts(SwapChainProbeResult result)
    {
        var byMoves = string.Join(
            ", ",
            result.ClosedByMoves.Select((count, index) =>
                (index + 1).ToString(CultureInfo.InvariantCulture) + " move(s) "
                + count.ToString(CultureInfo.InvariantCulture)));
        return $"open before {result.OpenBefore.ToString(CultureInfo.InvariantCulture)}; closed with {byMoves}; "
            + $"still open {result.StillOpen.ToString(CultureInfo.InvariantCulture)} "
            + $"(search cut off by the budget for {result.BudgetExhausted.ToString(CultureInfo.InvariantCulture)}); "
            + $"rule checks {result.Probes.ToString(CultureInfo.InvariantCulture)}; hard violations "
            + $"{result.LegalityBefore.ToString(CultureInfo.InvariantCulture)} -> "
            + result.LegalityAfter.ToString(CultureInfo.InvariantCulture)
            + $"; tolerated soft deviations of the plan {result.ToleratedDeviations.ToString(CultureInfo.InvariantCulture)}"
            + (result.Variant.PackageRestMayShorten
                ? $"; package-rest exceptions {result.PackageRestExceptions.Sum().ToString(CultureInfo.InvariantCulture)}"
                : string.Empty);
    }

    private static string Quality(AutofillMetrics metrics)
        => $"unfilled {metrics.Coverage.UnfilledShifts.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"rest violations {metrics.Legality.RestViolations.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"night-to-early {metrics.Legality.NightToEarlyViolations.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"packages {metrics.Packages.Items.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"ideal package share {metrics.Packages.IdealShare.ToString("0.000", CultureInfo.InvariantCulture)}, "
            + $"mixed packages {metrics.Packages.MixedTypeCount.ToString(CultureInfo.InvariantCulture)}, "
            + $"unexplained shortenings {metrics.Packages.UnexplainedShortenings.ToString(CultureInfo.InvariantCulture)}, "
            + $"ideal transition rate {metrics.Rotation.Spec.IdealTransitionRate.ToString("0.000", CultureInfo.InvariantCulture)}, "
            + $"unforced deviations {metrics.Rotation.Spec.UnforcedDeviations.ToString(CultureInfo.InvariantCulture)}";
}
