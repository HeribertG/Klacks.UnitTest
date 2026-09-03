// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Coverage-matrix guard for the region packages shipped under Klacks.Api/deploy/onprem/regions.
/// It builds an occupancy matrix — one cell per (scope, location, field) that a package actually
/// fills — and diffs it against the checked-in expectation file, so two drifts that neither the
/// parse test nor the semantic startup test can see become visible: a compliance or preset field
/// that the schema offers but NO package seeds (an evaluator running unseeded, e.g. compliance-level
/// counterRules today), and a field that quietly loses its last seeding package. A field counts as
/// occupied when its JSON member is present and non-null; an empty list counts as empty, because a
/// zero-length entity import seeds no rows. The declared field lists are additionally checked against
/// the real RegionSetup DTO properties, so a new schema field cannot slip past the matrix unnoticed.
/// </summary>

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.DTOs.Setup;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionPackageCoverageMatrixTests
{
    private const string ExpectationFileName = "RegionPackageCoverageMatrix.expected.json";
    private const string ApiProjectFolder = "Klacks.Api";
    private const string DeployFolder = "deploy";
    private const string OnPremFolder = "onprem";
    private const string RegionsFolder = "regions";
    private const string RegionFilePattern = "*.json";
    private const string RepoRootRelativeToTestSource = "../../..";

    private const char CellSeparator = '|';
    private const char LocationSeparator = '/';
    private const string UnnamedPreset = "(unnamed)";

    private const string ProfileScope = "profile";
    private const string WorktimeScope = "worktime";
    private const string ComplianceScope = "compliance";
    private const string EnforcementScope = "enforcement";
    private const string EnforcementRulesScope = "enforcementRules";
    private const string IndustryProfileScope = "industryProfile";
    private const string PresetScope = "preset";

    private const string WorktimeMember = "worktime";
    private const string ComplianceMember = "compliance";
    private const string EnforcementMember = "enforcement";
    private const string RulesMember = "rules";
    private const string IndustryProfilesMember = "industryProfiles";
    private const string SchedulingRulePresetsMember = "schedulingRulePresets";
    private const string NameMember = "name";

    private const string CountryPackageCountProperty = "countryPackageCount";
    private const string IndustryBlockCountProperty = "industryBlockCount";
    private const string PresetCountProperty = "presetCount";
    private const string FieldOccupancyCountsProperty = "fieldOccupancyCounts";
    private const string UnseededFieldsProperty = "unseededFields";
    private const string OccupiedCellsProperty = "occupiedCells";

    private static readonly string[] ProfileFields =
    [
        "version", "region", "languages", "locale", "calendar", WorktimeMember, "surcharges",
        "export", ComplianceMember, IndustryProfilesMember, "activeIndustries", "package",
        "macros", "seedDemoData", "seedDemoShiftsAndGroups"
    ];

    private static readonly string[] WorktimeFields =
    [
        "maximumHours", "minimumHours", "fullTime", "guaranteedHours", "defaultWorkingHours",
        "overtimeThreshold", "vacationDaysPerYear", "maxDailyHours", "maxWeeklyHours",
        "maxConsecutiveDays", "minRestDays", "minPauseHours"
    ];

    private static readonly string[] ComplianceFields =
    [
        "qualifications", EnforcementMember, "rosterPublication", "compensatoryRest", "periodCaps",
        "restDayRotations", "counterRules", "restrictedTimeWindows"
    ];

    private static readonly string[] EnforcementFields = ["defaultMode", RulesMember, "allowSupervisorOverride"];

    private static readonly string[] EnforcementRuleFields =
    [
        "maxDailyHours", "maxWeeklyHours", "minRestHours", "minRestDays", "maxConsecutiveDays",
        "periodCap", "rollingAverage", "restDayRotation", "counterRule", "compensatoryRest",
        "restrictedTimeWindow"
    ];

    private static readonly string[] IndustryProfileFields =
    [
        SchedulingRulePresetsMember, "qualificationCatalog", "periodCaps", "restDayRotations", "counterRules"
    ];

    private static readonly string[] PresetFields =
    [
        NameMember, "maxWorkDays", "minRestDays", "minPauseHours", "maxOptimalGap", "maxDailyHours",
        "maxWeeklyHours", "maxConsecutiveDays", "defaultWorkingHours", "overtimeThreshold",
        "guaranteedHours", "maximumHours", "minimumHours", "fullTimeHours", "vacationDaysPerYear",
        "nightRate", "holidayRate", "we1Rate", "we2Rate", "we3Rate", "nightStart", "nightEnd",
        "performsShiftWork", "overtime", "rateRevisions"
    ];

    private static readonly (string Scope, Type DtoType, string[] Fields)[] Scopes =
    [
        (ProfileScope, typeof(RegionSetupProfile), ProfileFields),
        (WorktimeScope, typeof(RegionSetupWorktime), WorktimeFields),
        (ComplianceScope, typeof(RegionSetupCompliance), ComplianceFields),
        (EnforcementScope, typeof(RegionSetupEnforcement), EnforcementFields),
        (EnforcementRulesScope, typeof(RegionSetupEnforcementRules), EnforcementRuleFields),
        (IndustryProfileScope, typeof(RegionSetupIndustryProfile), IndustryProfileFields),
        (PresetScope, typeof(RegionSetupSchedulingRulePreset), PresetFields)
    ];

    private readonly List<string> occupiedCells = [];

    private int countryPackageCount;

    private int industryBlockCount;

    private int presetCount;

    [OneTimeSetUp]
    public void BuildCoverageMatrix()
    {
        foreach (var path in RegionPackagePaths())
        {
            var countryCode = Path.GetFileNameWithoutExtension(path);
            using var package = JsonDocument.Parse(File.ReadAllText(path));
            var root = package.RootElement;
            this.countryPackageCount++;

            AddCells(this.occupiedCells, ProfileScope, countryCode, root, ProfileFields);
            AddCells(this.occupiedCells, WorktimeScope, countryCode, Member(root, WorktimeMember), WorktimeFields);

            var compliance = Member(root, ComplianceMember);
            AddCells(this.occupiedCells, ComplianceScope, countryCode, compliance, ComplianceFields);

            var enforcement = Member(compliance, EnforcementMember);
            AddCells(this.occupiedCells, EnforcementScope, countryCode, enforcement, EnforcementFields);
            AddCells(this.occupiedCells, EnforcementRulesScope, countryCode, Member(enforcement, RulesMember), EnforcementRuleFields);

            this.AddIndustryCells(countryCode, Member(root, IndustryProfilesMember));
        }

        this.occupiedCells.Sort(StringComparer.Ordinal);
    }

    [Test]
    public void PackageCounts_MatchTheCheckedInExpectation()
    {
        using var expectation = LoadExpectation();
        var root = expectation.RootElement;

        this.countryPackageCount.ShouldBe(
            root.GetProperty(CountryPackageCountProperty).GetInt32(),
            $"The number of shipped region packages under {RegionPackageDirectory()} changed. "
            + $"Update '{CountryPackageCountProperty}' in {ExpectationFileName} together with the cell list.");
        this.industryBlockCount.ShouldBe(
            root.GetProperty(IndustryBlockCountProperty).GetInt32(),
            $"The number of industry blocks across all region packages changed. "
            + $"Update '{IndustryBlockCountProperty}' in {ExpectationFileName} together with the cell list.");
        this.presetCount.ShouldBe(
            root.GetProperty(PresetCountProperty).GetInt32(),
            $"The number of scheduling rule presets across all region packages changed. "
            + $"Update '{PresetCountProperty}' in {ExpectationFileName} together with the cell list.");
    }

    [Test]
    public void OccupiedCells_MatchTheCheckedInExpectation()
    {
        using var expectation = LoadExpectation();
        var expected = expectation.RootElement
            .GetProperty(OccupiedCellsProperty)
            .EnumerateArray()
            .Select(cell => cell.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var actual = this.occupiedCells.ToHashSet(StringComparer.Ordinal);

        var newlyOccupied = actual.Except(expected, StringComparer.Ordinal).OrderBy(cell => cell, StringComparer.Ordinal).ToList();
        var newlyEmpty = expected.Except(actual, StringComparer.Ordinal).OrderBy(cell => cell, StringComparer.Ordinal).ToList();

        (newlyOccupied.Count + newlyEmpty.Count).ShouldBe(0, DescribeCellDrift(newlyOccupied, newlyEmpty));
    }

    [Test]
    public void FieldOccupancyCounts_MatchTheCheckedInExpectation()
    {
        using var expectation = LoadExpectation();
        var expected = expectation.RootElement
            .GetProperty(FieldOccupancyCountsProperty)
            .EnumerateObject()
            .ToDictionary(field => field.Name, field => field.Value.GetInt32(), StringComparer.Ordinal);
        var actual = this.CountOccupancyPerField();

        var drift = new StringBuilder();
        foreach (var field in expected.Keys.Union(actual.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            var expectedCount = expected.TryGetValue(field, out var fromFile) ? fromFile : -1;
            var actualCount = actual.TryGetValue(field, out var fromPackages) ? fromPackages : -1;
            if (expectedCount != actualCount)
            {
                drift.AppendLine($"  {DescribeField(field)}: expected {expectedCount} seeding locations, found {actualCount}.");
            }
        }

        drift.Length.ShouldBe(
            0,
            $"The field occupancy summary in {ExpectationFileName} no longer matches the shipped region packages:"
            + Environment.NewLine + drift);
    }

    [Test]
    public void UnseededFields_AreListedExplicitlyWithAReason()
    {
        using var expectation = LoadExpectation();
        var documented = expectation.RootElement
            .GetProperty(UnseededFieldsProperty)
            .EnumerateObject()
            .ToDictionary(field => field.Name, field => field.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
        var actual = this.CountOccupancyPerField()
            .Where(field => field.Value == 0)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal);

        var drift = new StringBuilder();
        foreach (var field in actual.Except(documented.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            drift.AppendLine(
                $"  {DescribeField(field)} lost its last seeding location and is now seeded by no package at all. "
                + $"Add it to '{UnseededFieldsProperty}' in {ExpectationFileName} with the reason why that gap is acceptable.");
        }

        foreach (var field in documented.Keys.Except(actual, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            drift.AppendLine(
                $"  {DescribeField(field)} is documented as seeded by no package, but a package now fills it. "
                + $"Remove it from '{UnseededFieldsProperty}' in {ExpectationFileName}.");
        }

        foreach (var field in documented.Where(field => string.IsNullOrWhiteSpace(field.Value)).OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            drift.AppendLine($"  {DescribeField(field.Key)} is listed as unseeded without a reason. A deliberate gap must state why.");
        }

        drift.Length.ShouldBe(
            0,
            $"The documented coverage gaps in {ExpectationFileName} no longer match the shipped region packages:"
            + Environment.NewLine + drift);
    }

    [Test]
    public void DeclaredFieldSurface_MatchesTheRegionSetupDtoProperties()
    {
        var drift = new StringBuilder();
        foreach (var (scope, dtoType, fields) in Scopes)
        {
            var dtoFields = dtoType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => ToJsonMemberName(property.Name))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var added in dtoFields.Except(fields, StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal))
            {
                drift.AppendLine(
                    $"  {dtoType.Name} gained property '{added}'. Add it to the '{scope}' field list of this test "
                    + $"and capture its occupancy in {ExpectationFileName}, otherwise nobody notices that no package seeds it.");
            }

            foreach (var removed in fields.Except(dtoFields, StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal))
            {
                drift.AppendLine(
                    $"  {dtoType.Name} no longer has property '{removed}'. Remove it from the '{scope}' field list "
                    + $"of this test and from {ExpectationFileName}.");
            }
        }

        drift.Length.ShouldBe(
            0,
            "The coverage matrix no longer covers the full region setup schema:" + Environment.NewLine + drift);
    }

    private static void AddCells(List<string> cells, string scope, string location, JsonElement? node, IReadOnlyList<string> fields)
    {
        foreach (var field in fields)
        {
            if (IsOccupied(Member(node, field)))
            {
                cells.Add($"{scope}{CellSeparator}{location}{CellSeparator}{field}");
            }
        }
    }

    private static string DescribeCellDrift(IReadOnlyList<string> newlyOccupied, IReadOnlyList<string> newlyEmpty)
    {
        var message = new StringBuilder();
        message.AppendLine($"The region package coverage matrix drifted from {ExpectationFileName}.");
        AppendCells(message, newlyOccupied, "NEWLY SEEDED");
        AppendCells(message, newlyEmpty, "NEWLY EMPTY");
        message.AppendLine("Review whether the change is intended, then update the expectation file accordingly.");
        return message.ToString();
    }

    private static void AppendCells(StringBuilder message, IReadOnlyList<string> cells, string direction)
    {
        if (cells.Count == 0)
        {
            return;
        }

        message.AppendLine($"{direction} ({cells.Count}):");
        foreach (var cell in cells)
        {
            message.AppendLine($"  {DescribeCell(cell)}");
        }
    }

    private static string DescribeCell(string cell)
    {
        var parts = cell.Split(CellSeparator);
        var scope = parts[0];
        var location = parts[1].Split(LocationSeparator);
        var field = parts[2];

        var description = new StringBuilder($"country '{location[0]}'");
        if (location.Length > 1)
        {
            description.Append($", industry '{location[1]}'");
        }

        if (location.Length > 2)
        {
            description.Append($", preset '{string.Join(LocationSeparator, location.Skip(2))}'");
        }

        return description.Append($", field '{scope}.{field}'").ToString();
    }

    private static string DescribeField(string field)
    {
        var parts = field.Split(CellSeparator);
        return $"Field '{parts[0]}.{parts[1]}'";
    }

    private static bool IsOccupied(JsonElement? value) =>
        value is { } element
        && element.ValueKind != JsonValueKind.Null
        && element.ValueKind != JsonValueKind.Undefined
        && (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 0);

    private static JsonDocument LoadExpectation()
    {
        var path = ExpectationFilePath();
        File.Exists(path).ShouldBeTrue($"coverage matrix expectation not found at {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement? Member(JsonElement? node, string name)
    {
        if (node is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string ToJsonMemberName(string propertyName) =>
        char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    private static IEnumerable<string> RegionPackagePaths() =>
        Directory.GetFiles(RegionPackageDirectory(), RegionFilePattern).OrderBy(path => path, StringComparer.Ordinal);

    private static string RegionPackageDirectory([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, RepoRootRelativeToTestSource));
        return Path.Combine(repoRoot, ApiProjectFolder, DeployFolder, OnPremFolder, RegionsFolder);
    }

    private static string ExpectationFilePath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, ExpectationFileName);

    private void AddIndustryCells(string countryCode, JsonElement? industryProfiles)
    {
        if (industryProfiles is not { ValueKind: JsonValueKind.Object } profiles)
        {
            return;
        }

        foreach (var industry in profiles.EnumerateObject().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var location = $"{countryCode}{LocationSeparator}{industry.Name}";
            this.industryBlockCount++;
            AddCells(this.occupiedCells, IndustryProfileScope, location, industry.Value, IndustryProfileFields);

            if (Member(industry.Value, SchedulingRulePresetsMember) is not { ValueKind: JsonValueKind.Array } presets)
            {
                continue;
            }

            foreach (var preset in presets.EnumerateArray())
            {
                var name = Member(preset, NameMember);
                var presetName = name is { ValueKind: JsonValueKind.String } ? name.Value.GetString()! : UnnamedPreset;
                this.presetCount++;
                AddCells(this.occupiedCells, PresetScope, $"{location}{LocationSeparator}{presetName}", preset, PresetFields);
            }
        }
    }

    private Dictionary<string, int> CountOccupancyPerField()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (scope, _, fields) in Scopes)
        {
            foreach (var field in fields)
            {
                counts[$"{scope}{CellSeparator}{field}"] = 0;
            }
        }

        foreach (var cell in this.occupiedCells)
        {
            var parts = cell.Split(CellSeparator);
            var key = $"{parts[0]}{CellSeparator}{parts[2]}";
            counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        return counts;
    }
}
