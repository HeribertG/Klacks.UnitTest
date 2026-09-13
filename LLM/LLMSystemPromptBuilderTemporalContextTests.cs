// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that Klacksy is told what "now" is: the volatile temporal block carries the company's own
/// calendar day, wall-clock minute and IANA zone, with the weekday rendered in the user's language
/// (German, Thai and Arabic all render Gregorian day names), while the stable prompt carries only the
/// rules for resolving relative dates - a value that changes every minute must never sit in the
/// prompt-cached stable segment.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class LLMSystemPromptBuilderTemporalContextTests
{
    private static readonly DateTimeOffset SaturdayNoonUtc = new(2026, 9, 12, 12, 5, 0, TimeSpan.Zero);

    private IPromptTranslationProvider _translationProvider = null!;

    [SetUp]
    public void Setup()
    {
        _translationProvider = Substitute.For<IPromptTranslationProvider>();
        _translationProvider.GetTranslationsAsync(Arg.Any<string>()).Returns(Translations());
    }

    private static Dictionary<string, string> Translations() => new()
    {
        ["Intro"] = "Intro",
        ["ToolUsageRules"] = "Rules",
        ["HeaderUserContext"] = "User Context",
        ["LabelUserId"] = "User ID",
        ["LabelPermissions"] = "Permissions",
        ["SettingsNoPermission"] = "No settings permission",
        ["SettingsViewOnly"] = "View only"
    };

    private static LLMContext Context(string language) => new()
    {
        UserId = "user-1",
        UserRights = new List<string> { "CanViewSettings", "CanEditSettings" },
        AvailableFunctions = new List<LLMFunction>(),
        Language = language
    };

    private LLMSystemPromptBuilder Builder(string timeZoneId)
    {
        var clock = new FixedCompanyClock(SaturdayNoonUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
        return new LLMSystemPromptBuilder(_translationProvider, clock);
    }

    [Test]
    public async Task BuildTemporalContextAsync_RendersIsoDate_LocalTime_AndIanaZone()
    {
        var result = await Builder("Asia/Tokyo").BuildTemporalContextAsync(Context("en"));

        result.ShouldNotBeNull();
        result.ShouldContain("2026-09-12");
        result.ShouldContain("21:05");
        result.ShouldContain("Asia/Tokyo");
    }

    [Test]
    public async Task BuildTemporalContextAsync_GermanUser_RendersGermanWeekday()
    {
        var result = await Builder("Europe/Vienna").BuildTemporalContextAsync(Context("de"));

        result.ShouldNotBeNull();
        result.ShouldContain("2026-09-12");
        result.ShouldContain("Samstag");
    }

    [Test]
    public async Task BuildTemporalContextAsync_ThaiUser_RendersThaiWeekday_WithGregorianYear()
    {
        var result = await Builder("Asia/Bangkok").BuildTemporalContextAsync(Context("th"));

        result.ShouldNotBeNull();
        result.ShouldContain("2026-09-12");
        result.ShouldContain("วันเสาร์");
        result.ShouldNotContain("2569");
    }

    [Test]
    public async Task BuildTemporalContextAsync_ArabicUser_RendersArabicWeekday()
    {
        var result = await Builder("Africa/Cairo").BuildTemporalContextAsync(Context("ar"));

        result.ShouldNotBeNull();
        result.ShouldContain("2026-09-12");
        result.ShouldContain("السبت");
    }

    [Test]
    public async Task BuildTemporalContextAsync_UnknownLanguage_FallsBackToEnglishWeekday_NotGerman()
    {
        var result = await Builder("Europe/Vienna").BuildTemporalContextAsync(Context("ru"));

        result.ShouldNotBeNull();
        result.ShouldContain("Saturday");
        result.ShouldNotContain("Samstag");
    }

    [Test]
    public async Task BuildSystemPromptAsync_CarriesTheRules_ButNoVolatileClockValue()
    {
        var prompt = await Builder("Europe/Vienna").BuildSystemPromptAsync(Context("en"));

        prompt.ShouldContain("TEMPORAL CONTEXT");
        prompt.ShouldContain("yyyy-MM-dd");
        prompt.ShouldNotContain("14:05");
        prompt.ShouldNotContain("2026-09-12");
    }

    [Test]
    public async Task BuildTemporalContextAsync_NonConversationalTurn_ReturnsNull()
    {
        var context = Context("en");
        context.IsNonConversational = true;

        var result = await Builder("Europe/Vienna").BuildTemporalContextAsync(context);

        result.ShouldBeNull();
    }
}
