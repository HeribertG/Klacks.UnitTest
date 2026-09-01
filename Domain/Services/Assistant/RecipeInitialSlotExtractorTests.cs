// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the W5.3 first-message slot extraction: times, dates and weekdays are read from
/// the message before the recipe plan asks, everything else stays empty so the plan asks normally.
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
