// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the dispatch-time date gate in SkillParameterTypeValidator. The gate BLOCKS a skill
/// call (SkillExecutorService turns a type error into SkillResult.Error), so it must accept at least
/// everything SkillCalendarStringParser accepts for any supported language - otherwise a Japanese
/// user's "2026/03/04" never reaches the parser at all. Written dates are therefore still gated
/// against every supported culture; only relative day words are scoped to the caller's language, and
/// the gate must then agree word for word with SkillDateParser given that same language.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillParameterTypeValidatorDateGateTests
{
    private const string DateParameterName = "date";
    private const string TimeParameterName = "time";

    private static readonly DateTime CompanyToday = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestCase("2026-03-04", "th")]
    [TestCase("2026/03/04", "ja")]
    [TestCase("2026/3/4", "zh-CN")]
    [TestCase("12.09.2026", "ar")]
    [TestCase("12.09.2569", "th")]
    [TestCase("04.03.2026", "de")]
    [TestCase("03/04/2026", "en")]
    [TestCase("03/04/2026", "fr")]
    [TestCase("04-03-2026", "nl")]
    public void EveryValueTheParserAccepts_PassesTheDispatchGate(string raw, string language)
    {
        SkillCalendarStringParser.TryParseDateOnly(raw, language, out _).ShouldBeTrue(
            $"The parser must accept '{raw}' for language '{language}' - otherwise this case tests nothing.");

        var errors = SkillParameterTypeValidator.Validate(
            DescriptorWith(DateParameterName, SkillParameterType.Date),
            new Dictionary<string, object> { [DateParameterName] = raw });

        errors.ShouldBeEmpty();
    }

    [TestCase("14:30")]
    [TestCase("14:30:00")]
    public void TimeValues_PassTheDispatchGate(string raw)
    {
        var errors = SkillParameterTypeValidator.Validate(
            DescriptorWith(TimeParameterName, SkillParameterType.Time),
            new Dictionary<string, object> { [TimeParameterName] = raw });

        errors.ShouldBeEmpty();
    }

    [Test]
    public void UnparsableValue_IsStillRejected()
    {
        var errors = SkillParameterTypeValidator.Validate(
            DescriptorWith(DateParameterName, SkillParameterType.Date),
            new Dictionary<string, object> { [DateParameterName] = "whenever" });

        errors.ShouldNotBeEmpty();
    }

    [Test]
    public void AllSupportedCultures_CoverEveryLanguageTheParserKnows()
    {
        var covered = SkillDateCultureResolver.AllSupportedCultures
            .Select(culture => (culture.Name, culture.DateTimeFormat.Calendar.GetType()))
            .ToHashSet();

        foreach (var language in new[] { "th", "ja", "ar", "de", "en", "fr", "pt", "zh-CN", "zh-TW" })
        {
            foreach (var culture in SkillDateCultureResolver.CulturesFor(language))
            {
                covered.ShouldContain(
                    (culture.Name, culture.DateTimeFormat.Calendar.GetType()),
                    $"Culture '{culture.Name}' of language '{language}' is missing from the gate's " +
                    "culture list, so a value the parser accepts would be blocked before dispatch.");
            }
        }
    }

    [TestCase("today")]
    [TestCase("heute")]
    [TestCase("明日")]
    [TestCase("พรุ่งนี้")]
    [TestCase("mañana")]
    [TestCase("gestern")]
    [TestCase("昨日")]
    [TestCase("hôm qua")]
    public void RelativeDayWords_PassTheDispatchGate(string raw)
    {
        var errors = SkillParameterTypeValidator.Validate(
            DescriptorWith(DateParameterName, SkillParameterType.Date),
            new Dictionary<string, object> { [DateParameterName] = raw });

        errors.ShouldBeEmpty();
    }

    [TestCase("hier", "de", false, 0)]
    [TestCase("hier", "fr", true, SkillRelativeDayWords.YesterdayOffset)]
    [TestCase("\u660e\u65e5", "ja", true, SkillRelativeDayWords.TomorrowOffset)]
    [TestCase("\u660e\u65e5", "de", false, 0)]
    [TestCase("morgen", "de", true, SkillRelativeDayWords.TomorrowOffset)]
    [TestCase("morgen", "nl", true, SkillRelativeDayWords.TomorrowOffset)]
    [TestCase("tomorrow", "ja", true, SkillRelativeDayWords.TomorrowOffset)]
    [TestCase("today", "th", true, SkillRelativeDayWords.TodayOffset)]
    [TestCase("ma\u00f1ana", "es", true, SkillRelativeDayWords.TomorrowOffset)]
    [TestCase("ma\u00f1ana", "de", false, 0)]
    [TestCase("gestern", "de", true, SkillRelativeDayWords.YesterdayOffset)]
    [TestCase("gestern", "fr", false, 0)]
    public void TheGateAndTheParser_AgreeWordForWord_ForTheSameLanguage(
        string raw, string language, bool accepted, int expectedOffset)
    {
        var errors = SkillParameterTypeValidator.Validate(
            DescriptorWith(DateParameterName, SkillParameterType.Date),
            new Dictionary<string, object> { [DateParameterName] = raw },
            language);

        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, CompanyToday, language);

        if (accepted)
        {
            errors.ShouldBeEmpty($"'{raw}' is a relative day word for '{language}'");
            invalid.ShouldBeFalse();
            value.ShouldBe(CompanyToday.AddDays(expectedOffset));
        }
        else
        {
            errors.ShouldNotBeEmpty(
                $"'{raw}' means nothing to a '{language}' user and must be refused at dispatch, not " +
                "silently resolved to a day");
            value.ShouldBeNull();
            invalid.ShouldBeTrue();
        }
    }

    [TestCase("hier", SkillRelativeDayWords.YesterdayOffset)]
    [TestCase("\u660e\u65e5", SkillRelativeDayWords.TomorrowOffset)]
    public void WithoutALanguage_TheUnionIsStillUsed(string raw, int expectedOffset)
    {
        var errors = SkillParameterTypeValidator.Validate(
            DescriptorWith(DateParameterName, SkillParameterType.Date),
            new Dictionary<string, object> { [DateParameterName] = raw });

        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, CompanyToday);

        errors.ShouldBeEmpty();
        invalid.ShouldBeFalse();
        value.ShouldBe(CompanyToday.AddDays(expectedOffset));
    }

    [Test]
    public void ANonDateWord_StillFailsTheDispatchGate()
    {
        var errors = SkillParameterTypeValidator.Validate(
            DescriptorWith(DateParameterName, SkillParameterType.Date),
            new Dictionary<string, object> { [DateParameterName] = "irgendwann" });

        errors.ShouldNotBeEmpty();
    }

    private static SkillDescriptor DescriptorWith(string parameterName, SkillParameterType type) =>
        new(
            nameof(SkillParameterTypeValidatorDateGateTests),
            string.Empty,
            SkillCategory.Query,
            [new SkillParameter(parameterName, string.Empty, type, false)],
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null);
}