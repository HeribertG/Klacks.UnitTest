// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Klacks.Api.Domain.Services.RouteOptimization.VisitScheduling;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.RouteOptimization.VisitScheduling;

/// <summary>
/// Parity of the backend container visit schedule with the Ui. The cases and their expected values come from the Ui
/// characterization spec (container-visit-schedule.characterization.spec.ts), which runs them through the production
/// ContainerTemplateRouteService.applyOptimizedRoute; a byte-identical copy of the case file lives in Klacks.Ui and both
/// sides pin the same SHA-256. Each case runs ContainerVisitScheduler, ContainerAbsenceTimeBlocks and
/// ContainerTemplateItemOrder the way ContainerRouteWriteService combines them and renders the result in the Ui's text
/// form (clock times HH:mm:ss, travel HH:MM), so every expected string must match exactly.
/// </summary>
[TestFixture]
public class ContainerVisitScheduleCharacterizationTests
{
    private const string CasesSha256 = "43b077bb6c105ebe94690566a83459003ad75d465ec98419b45b1b59df2df9e7";
    private const string ClockFormat = "HH:mm:ss";
    private const int MinimumCaseCount = 14;

    private static readonly Lazy<string> RawCases = new(() =>
        File.ReadAllText(CasesPath()).Replace("\r\n", "\n"));

    [Test]
    public void CaseFile_MatchesTheSha256PinnedByTheUiSpec()
    {
        // Act
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RawCases.Value)));

        // Assert
        hash.ShouldBe(CasesSha256);
    }

    [Test]
    public void CaseFile_HasAtLeastFourteenUniquelyNamedCases()
    {
        // Act
        var names = LoadCases().Select(c => c!["name"]!.GetValue<string>()).ToList();

        // Assert
        names.Count.ShouldBeGreaterThanOrEqualTo(MinimumCaseCount);
        names.Distinct().Count().ShouldBe(names.Count);
    }

    [TestCaseSource(nameof(CaseNames))]
    public void Schedule_MatchesTheUiOracle(string caseName)
    {
        // Arrange
        var testCase = LoadCases().Single(c => c!["name"]!.GetValue<string>() == caseName)!;

        // Act
        var actual = RunCase(testCase);

        // Assert
        var expected = testCase["expected"];
        JsonNode.DeepEquals(actual, expected).ShouldBeTrue(
            $"expected {expected?.ToJsonString()}{Environment.NewLine}actual   {actual.ToJsonString()}");
    }

    private static IEnumerable<string> CaseNames() =>
        LoadCases().Select(c => c!["name"]!.GetValue<string>());

    private static JsonArray LoadCases() => JsonNode.Parse(RawCases.Value)!["cases"]!.AsArray();

    private static string CasesPath([CallerFilePath] string sourceFile = "")
    {
        var testProjectRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "..", ".."));
        return Path.Combine(testProjectRoot, "TestData", "ContainerVisitSchedule", "container-visit-schedule-cases.json");
    }

    private static JsonObject RunCase(JsonNode testCase)
    {
        var items = testCase["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        var visitJson = items.Where(i => Text(i, "shiftId") != null).ToList();
        var absenceJson = items.Where(i => Text(i, "absenceId") != null).ToList();
        var absences = absenceJson.Select(ToAbsenceItem).ToList();
        var route = testCase["route"]!;

        var input = new VisitScheduleInput(
            ContainerVisitScheduler.ClockMinutes(Clock(Text(testCase, "containerFromTime"))!.Value),
            route["optimizedRoute"]!.AsArray().Select((s, index) => ToRouteStep(s!, index)).ToList(),
            Duration(Text(route, "travelTimeFromStartBase")!),
            Duration(Text(route, "travelTimeToEndBase")!),
            route["distanceToEndBaseKm"]!.GetValue<double>(),
            visitJson.Select(ToVisitItem).ToList(),
            ContainerAbsenceTimeBlocks.BuildFixedIntervals(absences));

        var scheduled = ContainerVisitScheduler.Schedule(input);

        var lastItemByShiftId = new Dictionary<string, JsonObject>();
        foreach (var visit in visitJson)
        {
            lastItemByShiftId[Text(visit, "shiftId")!] = visit;
        }

        var visitRows = scheduled.Select(v => ToVisitRow(v, lastItemByShiftId[v.Item.ShiftId.ToString()])).ToList();
        var absenceRows = absenceJson.Select(ToAbsenceRow).ToList();
        var ordered = ContainerTemplateItemOrder.Interleave(absenceRows, visitRows, row => row.StartMinutes);

        var timeBlocks = new JsonArray(ContainerAbsenceTimeBlocks.BuildTimeBlocks(absences).Select(b => (JsonNode)new JsonObject
        {
            ["id"] = b.Id.ToString(),
            ["fixedStartTime"] = b.FixedStartTime?.ToString(ClockFormat, CultureInfo.InvariantCulture),
            ["fixedEndTime"] = b.FixedEndTime?.ToString(ClockFormat, CultureInfo.InvariantCulture),
            ["durationMinutes"] = (int)b.Duration.TotalMinutes,
            ["isMovable"] = b.IsMovable
        }).ToArray());

        return new JsonObject
        {
            ["items"] = ordered.Count > 0 ? new JsonArray(ordered.Select(r => (JsonNode)r.Output).ToArray()) : null,
            ["timeBlocks"] = timeBlocks
        };
    }

    private sealed record OutputRow(int StartMinutes, JsonObject Output);

    private static OutputRow ToVisitRow(ScheduledVisit visit, JsonObject source)
    {
        var start = ContainerVisitScheduler.ClockTime(visit.StartMinutes);
        var usesTimeRange = source["shift"]!["isTimeRange"]!.GetValue<bool>();
        var output = new JsonObject
        {
            ["key"] = Text(source, "shiftId"),
            ["timeRangeStartItem"] = start.ToString(ClockFormat, CultureInfo.InvariantCulture),
            ["timeRangeEndItem"] = ContainerVisitScheduler.ClockTime(visit.EndMinutes).ToString(ClockFormat, CultureInfo.InvariantCulture),
            ["travelTimeBefore"] = HoursMinutes(visit.TravelBeforeMinutes),
            ["travelTimeAfter"] = visit.TravelAfterMinutes.HasValue
                ? HoursMinutes(visit.TravelAfterMinutes.Value)
                : Text(source, "travelTimeAfter")
        };

        return new OutputRow(
            ContainerTemplateItemOrder.StartMinutes(usesTimeRange, Clock(Text(source, "startItem")), start),
            output);
    }

    private static OutputRow ToAbsenceRow(JsonObject source)
    {
        var output = new JsonObject
        {
            ["key"] = Text(source, "absenceId"),
            ["timeRangeStartItem"] = Text(source, "timeRangeStartItem"),
            ["timeRangeEndItem"] = Text(source, "timeRangeEndItem"),
            ["travelTimeBefore"] = Text(source, "travelTimeBefore"),
            ["travelTimeAfter"] = Text(source, "travelTimeAfter")
        };

        return new OutputRow(
            ContainerTemplateItemOrder.StartMinutes(false, Clock(Text(source, "startItem")), Clock(Text(source, "timeRangeStartItem"))),
            output);
    }

    private static VisitItem ToVisitItem(JsonObject item)
    {
        var shift = item["shift"];
        var mode = item["transportMode"];
        return new VisitItem(
            Guid.Parse(Text(item, "shiftId")!),
            shift != null,
            shift?["workTime"]!.GetValue<double>() ?? 0,
            Clock(Text(item, "briefingTime"))!.Value,
            Clock(Text(item, "debriefingTime"))!.Value,
            mode == null ? TransportMode.ByCar : (TransportMode)mode.GetValue<int>());
    }

    private static ContainerAbsenceItem ToAbsenceItem(JsonObject item) => new(
        Guid.Parse(Text(item, "absenceId")!),
        Clock(Text(item, "startItem")),
        Clock(Text(item, "endItem")),
        Clock(Text(item, "timeRangeStartItem")),
        Clock(Text(item, "timeRangeEndItem")));

    private static RouteStep ToRouteStep(JsonNode step, int index) => new(
        index + 1,
        string.Empty,
        string.Empty,
        0,
        0,
        Guid.Parse(Text(step, "shiftId")!),
        TransportMode.ByCar,
        0,
        Duration(Text(step, "travelTimeToNext")!));

    private static string? Text(JsonNode node, string property) => node[property]?.GetValue<string>();

    private static TimeOnly? Clock(string? text) =>
        text == null ? null : TimeOnly.ParseExact(text, ClockFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static TimeSpan Duration(string text) => TimeSpan.ParseExact(text, "c", CultureInfo.InvariantCulture);

    private static string HoursMinutes(int minutes) =>
        $"{(minutes / 60).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}:{(minutes % 60).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}";
}
