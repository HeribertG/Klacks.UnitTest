// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using System.Text.Json;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Reads a stored measurement of a run that already finished, so a later scenario can compare itself
/// against it without executing that run again. Scenario 6 needs this: its control run IS the
/// scenario-5 main run, which costs about 522 seconds and must not be repeated eight times.
/// <para>
/// It walks the JSON with <see cref="JsonDocument"/> instead of deserialising the metric record.
/// The file was written by another session, possibly against an older schema; a full-graph
/// deserialisation would let one unrelated field break the comparison, and a comparison that throws
/// looks exactly like one that found nothing.
/// </para>
/// <para>
/// WHERE IT LOOKS, in order. First the environment variable
/// <see cref="BaselineOverrideVariable"/>, which is how a run points at a copy that was secured
/// elsewhere — the phase-C artifacts of scenario 5 were secured outside the repository, and the live
/// folder is rewritten by every run of the same test. Then the live artifact folder of the baseline
/// scenario. The live folder is NOT durable: it is git-ignored and any rerun of that test overwrites
/// it. That is why a missing baseline is a reported state and never a silent zero.
/// </para>
/// </summary>
public static class BaselineMetricsReader
{
    /// <summary>Environment variable naming an explicit baseline file; checked before the artifact folder.</summary>
    public const string BaselineOverrideVariable = "KLACKS_AUTOFILL_BASELINE_METRICS";

    private const string MetricsExtension = ".metrics.json";

    private const string PathSeparator = " ; ";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads a baseline measurement, or reports why it could not.
    /// </summary>
    /// <param name="scenarioName">Artifact folder of the baseline run, for example scenario5</param>
    /// <param name="testName">Artifact file name of the baseline run, for example Scenario5a</param>
    /// <param name="runLabel">Run label of the baseline run, for example run1</param>
    public static BaselineMetricsLoad Load(string scenarioName, string testName, string runLabel)
    {
        var candidates = CandidatePaths(scenarioName, testName, runLabel);
        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null)
        {
            return new BaselineMetricsLoad(
                null,
                string.Join(PathSeparator, candidates),
                $"No baseline artifact found for {testName}.{runLabel}. Looked at: "
                + string.Join(PathSeparator, candidates)
                + $". Run that scenario once, or point {BaselineOverrideVariable} at a secured copy of its "
                + "metrics JSON.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(existing), DocumentOptions);
            return new BaselineMetricsLoad(Read(document.RootElement), existing, null);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new BaselineMetricsLoad(
                null,
                existing,
                $"The baseline artifact '{existing}' could not be read: {exception.Message}");
        }
    }

    /// <summary>Every path a baseline is looked for, in the order they are tried.</summary>
    /// <param name="scenarioName">Artifact folder of the baseline run</param>
    /// <param name="testName">Artifact file name of the baseline run</param>
    /// <param name="runLabel">Run label of the baseline run</param>
    public static IReadOnlyList<string> CandidatePaths(string scenarioName, string testName, string runLabel)
    {
        var paths = new List<string>();
        var overridePath = Environment.GetEnvironmentVariable(BaselineOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            paths.Add(overridePath);
        }

        paths.Add(Path.Combine(
            AutofillRepositoryPaths.ArtifactDirectory(scenarioName),
            $"{testName}.{runLabel}{MetricsExtension}"));
        return paths;
    }

    private static BaselineMetricsSnapshot Read(JsonElement root)
        => new(
            TestName: StringOf(root, "TestName"),
            RunLabel: StringOf(root, "RunLabel"),
            ShiftTypeCountPerEmployee: ReadShiftTypeCounts(root),
            HoursPerEmployee: ReadHours(root),
            Assignments: ReadAssignments(root));

    private static IReadOnlyList<EmployeeShiftTypeCounts> ReadShiftTypeCounts(JsonElement root)
    {
        if (!TryArray(root, "Fairness", "ShiftTypeCountPerEmployee", out var array))
        {
            return [];
        }

        return array
            .EnumerateArray()
            .Select(entry => new EmployeeShiftTypeCounts(
                StringOf(entry, "Employee"),
                IntOf(entry, "ListRank"),
                IntOf(entry, "Early"),
                IntOf(entry, "Late"),
                IntOf(entry, "Night")))
            .ToList();
    }

    private static IReadOnlyList<EmployeeHours> ReadHours(JsonElement root)
    {
        if (!TryArray(root, "Hours", "PerEmployee", out var array))
        {
            return [];
        }

        return array
            .EnumerateArray()
            .Select(entry => new EmployeeHours(
                StringOf(entry, "Employee"),
                IntOf(entry, "ListRank"),
                DoubleOf(entry, "GuaranteedHours"),
                DoubleOf(entry, "PlannedHours"),
                DoubleOf(entry, "FulfillmentPct")))
            .ToList();
    }

    private static IReadOnlyList<PlanAssignmentRow> ReadAssignments(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Assignments", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array
            .EnumerateArray()
            .Select(entry => new PlanAssignmentRow(
                StringOf(entry, "Employee"),
                DateOf(entry, "Date"),
                KindOf(entry, "SlotKind"),
                IntOf(entry, "Order"),
                GuidOf(entry, "ShiftRefId")))
            .ToList();
    }

    private static bool TryArray(JsonElement root, string parent, string child, out JsonElement array)
    {
        array = default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(parent, out var section))
        {
            return false;
        }

        if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty(child, out array))
        {
            return false;
        }

        return array.ValueKind == JsonValueKind.Array;
    }

    private static string StringOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int IntOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static double DoubleOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static DateOnly DateOf(JsonElement element, string name)
        => DateOnly.TryParse(
            StringOf(element, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : default;

    private static Guid GuidOf(JsonElement element, string name)
        => Guid.TryParse(StringOf(element, name), out var id) ? id : Guid.Empty;

    private static AutofillShiftKind KindOf(JsonElement element, string name)
        => Enum.TryParse<AutofillShiftKind>(StringOf(element, name), out var kind)
            ? kind
            : AutofillShiftKind.Early;
}
