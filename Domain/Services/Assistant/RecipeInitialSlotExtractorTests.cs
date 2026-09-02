// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the W5.3 first-message slot extraction: times, dates and weekdays are read from
/// the message before the recipe plan asks, everything else stays empty so the plan asks normally.
/// The precision block pins the false positives the pattern used to produce — a bare number is not a
/// clock time, and an impossible calendar date is not a date.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeInitialSlotExtractorTests
{
    [Test]
    public void Extract_FillsStartAndEndTime_FromHHmmClock()
    {
        var slots = RecipeInitialSlotExtractor.Extract(
            "Bitte Dienst von 08:00 bis 17:30 einplanen",
            ["startTime", "endTime"]);

        slots["startTime"].ShouldBe("08:00");
        slots["endTime"].ShouldBe("17:30");
    }

    [Test]
    public void Extract_FillsFromUntilTime_FromSingleTime()
    {
        var slots = RecipeInitialSlotExtractor.Extract(
            "Container ab 07:15 Uhr",
            ["fromTime", "untilTime"]);

        slots["fromTime"].ShouldBe("07:15");
        slots["untilTime"].ShouldBe("07:15");
    }

    [Test]
    public void Extract_FillsWeekdays_WithCanonicalGermanNames()
    {
        var slots = RecipeInitialSlotExtractor.Extract(
            "Schicht für Montag und Mittwoch",
            ["weekdays"]);

        slots["weekdays"].ShouldBe("Montag, Mittwoch");
    }

    [Test]
    public void Extract_FillsDates_FromIsoAndCompactGermanDates()
    {
        var iso = RecipeInitialSlotExtractor.Extract("ab 2026-09-01", ["startDate"]);
        iso["startDate"].ShouldBe("2026-09-01");

        var compact = RecipeInitialSlotExtractor.Extract("vom 1.9. bis 3.9.", ["fromDate", "untilDate"]);
        compact["fromDate"].ShouldBe("2026-09-01");
        compact["untilDate"].ShouldBe("2026-09-03");
    }

    [TestCase("Wir brauchen 5 Mitarbeiter")]
    [TestCase("Filiale mit PLZ 8001")]
    [TestCase("Plane 3 Dienste")]
    [TestCase("ab 2026-09-01")]
    [TestCase("vom 1.9. bis 3.9.")]
    public void Extract_DoesNotReadATime_FromABareNumber(string message)
    {
        var slots = RecipeInitialSlotExtractor.Extract(message, ["startTime", "endTime"]);

        slots.ShouldBeEmpty();
    }

    [TestCase("Dienst um 14:30", "14:30")]
    [TestCase("Dienst um 14 Uhr", "14:00")]
    [TestCase("Beginn um 7 Uhr", "07:00")]
    [TestCase("Beginn um 14.30 Uhr", "14:30")]
    [TestCase("Beginn um 14h30", "14:30")]
    public void Extract_ReadsATime_WhenAClockMarkerIsPresent(string message, string expected)
    {
        var slots = RecipeInitialSlotExtractor.Extract(message, ["startTime"]);

        slots["startTime"].ShouldBe(expected);
    }

    [Test]
    public void Extract_IgnoresAnImpossibleCalendarDate()
    {
        var slots = RecipeInitialSlotExtractor.Extract("ab 31.02.2026", ["startDate"]);

        slots.ShouldBeEmpty();
    }

    [Test]
    public void Extract_ReadsAnIsoDate_WithoutInventingATime()
    {
        var slots = RecipeInitialSlotExtractor.Extract("ab 2026-09-01", ["startDate", "startTime"]);

        slots["startDate"].ShouldBe("2026-09-01");
        slots.ContainsKey("startTime").ShouldBeFalse();
    }

    [Test]
    public void Extract_ReadsAWrittenGermanDate()
    {
        var slots = RecipeInitialSlotExtractor.Extract("ab 1. September 2026", ["startDate"]);

        slots["startDate"].ShouldBe("2026-09-01");
    }

    [Test]
    public void Extract_ReadsAFullNumericGermanDate()
    {
        var slots = RecipeInitialSlotExtractor.Extract("ab 28.02.2026", ["startDate"]);

        slots["startDate"].ShouldBe("2026-02-28");
    }

    [Test]
    public void Extract_DoesNotReadAWeekday_FromTheLowerCaseFillerWord()
    {
        var slots = RecipeInitialSlotExtractor.Extract(
            "so wie letzte Woche, do es bitte", ["weekdays"]);

        slots.ShouldBeEmpty();
    }

    [Test]
    public void Extract_ReadsWeekdayAbbreviations_InTheirWrittenForm()
    {
        var slots = RecipeInitialSlotExtractor.Extract("Schicht Mo und Mi", ["weekdays"]);

        slots["weekdays"].ShouldBe("Montag, Mittwoch");
    }

    [Test]
    public void Extract_DoesNotInventSlotsForEmptyMessage()
    {
        var slots = RecipeInitialSlotExtractor.Extract("", ["startTime", "groupName"]);

        slots.ShouldBeEmpty();
    }

    [Test]
    public void FindMentionedGroupName_PrefersLongestRealName()
    {
        var mentioned = RecipeInitialSlotExtractor.FindMentionedGroupName(
            "Zeig mir die Abwesenheiten der Gruppe Deutschschweiz Zürich",
            ["Deutsch", "Deutschschweiz Zürich"]);

        mentioned.ShouldBe("Deutschschweiz Zürich");
    }

    [Test]
    public void FindMentionedGroupName_ReturnsNullWhenNoGroupNameAppears()
    {
        RecipeInitialSlotExtractor.FindMentionedGroupName(
                "Zeig mir die Abwesenheiten", ["Deutschschweiz Zürich"])
            .ShouldBeNull();
    }
}
