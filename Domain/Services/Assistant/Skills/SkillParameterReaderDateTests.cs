// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the date/time paths of SkillParameterReader. Before the shared parsing policy the
/// reader called DateOnly.Parse/TimeOnly.Parse without a culture, so the same LLM argument produced a
/// different day in the production container (InvariantCulture) than on a developer machine (OS
/// culture). These tests pin that the result now depends only on the value and the user's language.
/// </summary>

using System.Globalization;
using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillParameterReaderDateTests
{
    private const string DateParameterName = "date";
    private const string TimeParameterName = "time";
    private const string SwissProcessCulture = "de-CH";
    private const string AmericanProcessCulture = "en-US";

    private static readonly string[] ProcessCultures = [AmericanProcessCulture, SwissProcessCulture];

    [TestCase("2026-03-04", null, 2026, 3, 4)]
    [TestCase("2026-03-04", "th", 2026, 3, 4)]
    [TestCase("04.03.2026", "de", 2026, 3, 4)]
    [TestCase("03/04/2026", "en", 2026, 3, 4)]
    [TestCase("03/04/2026", "fr", 2026, 4, 3)]
    public void DateOnly_IsIndependentOfTheProcessCulture(
        string raw, string? language, int year, int month, int day)
    {
        var parameters = new Dictionary<string, object>
        {
            [DateParameterName] = JsonSerializer.SerializeToElement(raw)
        };

        InEveryProcessCulture(() =>
        {
            var value = SkillParameterReader.Read<DateOnly?>(parameters, DateParameterName, null, language);

            value.ShouldBe(new DateOnly(year, month, day));
        });
    }

    [TestCase("2026-03-04", null)]
    [TestCase("04.03.2026", "de")]
    [TestCase("03/04/2026", "en")]
    [TestCase("03/04/2026", "fr")]
    public void DateOnlyAndDateTime_AgreeOnTheDay(string raw, string? language)
    {
        var parameters = new Dictionary<string, object>
        {
            [DateParameterName] = JsonSerializer.SerializeToElement(raw)
        };

        InEveryProcessCulture(() =>
        {
            var dateOnly = SkillParameterReader.Read<DateOnly?>(parameters, DateParameterName, null, language);
            var dateTime = SkillParameterReader.Read<DateTime?>(parameters, DateParameterName, null, language);

            dateTime!.Value.Kind.ShouldBe(DateTimeKind.Utc);
            DateOnly.FromDateTime(dateTime.Value).ShouldBe(dateOnly!.Value);
        });
    }

    [Test]
    public void TimeOnly_IsIndependentOfTheProcessCulture()
    {
        var parameters = new Dictionary<string, object>
        {
            [TimeParameterName] = JsonSerializer.SerializeToElement("14:30")
        };

        InEveryProcessCulture(() =>
        {
            var value = SkillParameterReader.Read<TimeOnly?>(parameters, TimeParameterName);

            value.ShouldBe(new TimeOnly(14, 30));
        });
    }

    [Test]
    public void UnparsableDate_ReturnsTheDefault_InsteadOfThrowing()
    {
        var parameters = new Dictionary<string, object> { [DateParameterName] = "whenever" };

        InEveryProcessCulture(() =>
            SkillParameterReader.Read<DateOnly?>(parameters, DateParameterName).ShouldBeNull());
    }

    private static void InEveryProcessCulture(Action assertion)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var cultureName in ProcessCultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                assertion();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
