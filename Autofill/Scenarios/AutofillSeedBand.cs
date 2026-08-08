// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Measures the seed band of one scenario and reports it. The specification assertions judge the run
/// of <see cref="AutofillSpecConstants.RandomSeed"/> alone; this class runs the same scenario once per
/// further baseline seed and reports how far the eight baseline numbers move when nothing but the seed
/// changes.
/// <para>
/// That span is the reason the guards are pinned as a band rather than as an exact value: the engine's
/// seed-to-seed spread moves several of the soft metrics further than a real code change does, so an
/// exact pin reports trajectory noise as a regression. Pinning the worst of the band makes a red mean
/// "worse than anything this engine produced", which is a statement about the code.
/// </para>
/// <para>
/// Diagnosis only. Nothing here is asserted, and the extra runs happen after the deterministic double
/// run has finished, so neither the A9 determinism verdict nor the A14 boundary fingerprint can be
/// disturbed by them.
/// </para>
/// </summary>
public static class AutofillSeedBand
{
    private const int SecondSeed = 43;

    private const int ThirdSeed = 44;

    private const string ArtifactExtension = ".band.json";

    private const string RoundTripFormat = "R";

    private const string ReportNote =
        "Diagnosis only, never asserted. 'worst' is the value a guard should be pinned to: the lowest forwardRate, "
        + "idealShare, topRanksPlannedHours and carryInOkCount and the highest mixedTypeCount, shortPackageShare, "
        + "packagesOverIdealLength, shiftKindSpread and monotonicityViolations over the seeds listed under 'samples'. "
        + "A guard still checks the run of the first seed only; the band decides how far that run may move before the "
        + "guard turns red.";

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The seeds a baseline band is measured over; the first one is the asserted run.</summary>
    public static IReadOnlyList<int> Seeds { get; } = [AutofillSpecConstants.RandomSeed, SecondSeed, ThirdSeed];

    /// <summary>
    /// Runs the scenario once per additional band seed, writes the band artifact and prints the band
    /// to the test output. Returns the assembled report so a caller can print more of it if it wants.
    /// </summary>
    /// <param name="definition">Scenario the asserted run used; only its seed is varied</param>
    /// <param name="assertedRunMetrics">Measurement of the asserted run, i.e. of the first seed</param>
    /// <param name="currentlyPinned">Values the scenario's baseline constants hold right now</param>
    /// <param name="scenarioName">Scenario name; becomes the artifact folder</param>
    /// <param name="testName">Test name; becomes the artifact file name</param>
    public static AutofillBandReport MeasureAndReport(
        AutofillScenarioDefinition definition,
        AutofillMetrics assertedRunMetrics,
        AutofillBandValues currentlyPinned,
        string scenarioName,
        string testName)
    {
        var hasCarryIn = definition.CarryIns.Count > 0;
        var samples = new List<AutofillBandSample>
        {
            new(Seeds[0], AutofillBandValues.From(assertedRunMetrics, hasCarryIn)),
        };

        foreach (var seed in Seeds.Skip(1))
        {
            var metrics = SeedBandRunner.Run(definition, seed, scenarioName, testName);
            samples.Add(new AutofillBandSample(seed, AutofillBandValues.From(metrics, hasCarryIn)));
        }

        var measured = samples.Select(s => s.Values).ToList();
        var report = new AutofillBandReport(
            Scenario: scenarioName,
            TestName: testName,
            MeasuredAtUtc: DateTime.UtcNow,
            Note: ReportNote,
            Samples: samples,
            Worst: AutofillBandValues.WorstOf(measured),
            Best: AutofillBandValues.BestOf(measured),
            CurrentlyPinned: currentlyPinned);

        Write(report);
        Print(report);

        return report;
    }

    /// <summary>Reads the values a scenario has pinned into the shape the report compares against.</summary>
    /// <param name="baseline">The seven shared pins of the scenario</param>
    /// <param name="carryInOkCount">The carry-in pin, or null in a scenario without a previous month</param>
    public static AutofillBandValues PinnedValuesOf(AutofillBaseline baseline, int? carryInOkCount)
        => new(
            ForwardRate: baseline.MinForwardRate,
            MixedTypeCount: baseline.MaxMixedTypeCount,
            ShortPackageShare: baseline.MaxShortPackageShare,
            PackagesOverIdealLength: baseline.MaxPackagesOverIdealLength,
            IdealShare: baseline.MinIdealShare,
            ShiftKindSpread: baseline.MaxShiftKindSpread,
            MonotonicityViolations: baseline.MaxMonotonicityViolations,
            TopRanksPlannedHours: baseline.MinTopRanksPlannedHours,
            CarryInOkCount: carryInOkCount);

    private static void Write(AutofillBandReport report)
    {
        var path = Path.Combine(
            AutofillRepositoryPaths.ArtifactDirectory(report.Scenario), report.TestName + ArtifactExtension);

        File.WriteAllText(path, JsonSerializer.Serialize(report, ReportJsonOptions));
        TestContext.Out.WriteLine($"[{report.Scenario}] band artifact: {path}");

        try
        {
            TestContext.AddTestAttachment(path);
        }
        catch (Exception)
        {
            // Attaching only works inside a running test. Writing the file is the contract; the
            // attachment is a convenience, and losing it must never fail the run.
        }
    }

    private static void Print(AutofillBandReport report)
    {
        foreach (var sample in report.Samples)
        {
            TestContext.Out.WriteLine(
                $"[{report.Scenario}] band seed {sample.Seed.ToString(CultureInfo.InvariantCulture)}: "
                + Describe(sample.Values));
        }

        TestContext.Out.WriteLine($"[{report.Scenario}] band worst (pin these): " + Describe(report.Worst));
        TestContext.Out.WriteLine($"[{report.Scenario}] band best: " + Describe(report.Best));
        TestContext.Out.WriteLine($"[{report.Scenario}] currently pinned: " + Describe(report.CurrentlyPinned));
    }

    private static string Describe(AutofillBandValues values)
    {
        var text = new StringBuilder()
            .Append("forwardRate=").Append(Number(values.ForwardRate))
            .Append(", mixedTypeCount=").Append(Number(values.MixedTypeCount))
            .Append(", shortPackageShare=").Append(Number(values.ShortPackageShare))
            .Append(", packagesOverIdealLength=").Append(Number(values.PackagesOverIdealLength))
            .Append(", idealShare=").Append(Number(values.IdealShare))
            .Append(", shiftKindSpread=").Append(Number(values.ShiftKindSpread))
            .Append(", monotonicityViolations=").Append(Number(values.MonotonicityViolations))
            .Append(", topRanksPlannedHours=").Append(Number(values.TopRanksPlannedHours));

        if (values.CarryInOkCount is not null)
        {
            text.Append(", carryInOkCount=").Append(Number(values.CarryInOkCount.Value));
        }

        return text.ToString();
    }

    /// <summary>
    /// Round-trip format, never a rounded one: a pin is copied from this output into a source literal,
    /// and a shortened share would silently pin a different number than the one measured.
    /// </summary>
    /// <param name="value">Measured share</param>
    private static string Number(double value) => value.ToString(RoundTripFormat, CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
