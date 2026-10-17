// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The explanation depth reaches the prompt in two places: the offer-first rule is part of the stable prompt for every
/// user (so the cached segment never varies by user), and the per-user style block lives in the volatile additions only
/// when a depth was resolved for the turn, including whether the user chose it and that an explicit request beats it.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class LLMSystemPromptBuilderExplainDepthTests
{
    private LLMSystemPromptBuilder _builder = null!;

    [SetUp]
    public void SetUp()
    {
        var translationProvider = Substitute.For<IPromptTranslationProvider>();
        translationProvider.GetTranslationsAsync(Arg.Any<string>()).Returns(new Dictionary<string, string>
        {
            { "Intro", "You are a helpful assistant." },
            { "ToolUsageRules", "Use tools when appropriate." },
            { "HeaderUserContext", "User Context" },
            { "LabelUserId", "User ID" },
            { "LabelPermissions", "Permissions" },
            { "SettingsNoPermission", "No settings permission" },
            { "SettingsViewOnly", "View settings only" }
        });
        _builder = new LLMSystemPromptBuilder(
            translationProvider,
            new FixedCompanyClock(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    private static LLMContext Context(ExplainDepthProfile? depth) => new()
    {
        UserId = Guid.NewGuid().ToString(),
        UserRights = new List<string>(),
        Language = "de",
        ExplainDepth = depth
    };

    [Test]
    public void BeginnerDepth_RendersBeginnerStyleAsEstimated()
    {
        var volatileBlock = LLMSystemPromptBuilder.BuildVolatileAdditions(
            Context(new ExplainDepthProfile(ExplainExperienceLevel.Beginner, false)));

        volatileBlock.ShouldNotBeNull();
        volatileBlock.ShouldContain(ExplainDepthPromptTexts.Header);
        volatileBlock.ShouldContain(ExplainDepthPromptTexts.BeginnerStyle);
        volatileBlock.ShouldNotContain(ExplainDepthPromptTexts.AdvancedStyle);
        volatileBlock.ShouldContain(string.Format(
            ExplainDepthPromptTexts.LevelLineFormat,
            ExplainDepthPromptTexts.LevelNameBeginner,
            ExplainDepthPromptTexts.SourceEstimated));
        volatileBlock.ShouldContain(ExplainDepthPromptTexts.ExplicitRequestRule);
        volatileBlock.ShouldContain(ExplainDepthPromptTexts.PersistentChangeHint);
    }

    [Test]
    public void AdvancedOverride_RendersAdvancedStyleAsChosenByTheUser()
    {
        var volatileBlock = LLMSystemPromptBuilder.BuildVolatileAdditions(
            Context(new ExplainDepthProfile(ExplainExperienceLevel.Advanced, true)));

        volatileBlock.ShouldNotBeNull();
        volatileBlock.ShouldContain(ExplainDepthPromptTexts.AdvancedStyle);
        volatileBlock.ShouldNotContain(ExplainDepthPromptTexts.BeginnerStyle);
        volatileBlock.ShouldContain(string.Format(
            ExplainDepthPromptTexts.LevelLineFormat,
            ExplainDepthPromptTexts.LevelNameAdvanced,
            ExplainDepthPromptTexts.SourceExplicitOverride));
        volatileBlock.ShouldContain(ExplainDepthPromptTexts.ExplicitRequestRule);
        volatileBlock.ShouldContain(ExplainDepthPromptTexts.PersistentChangeHint);
    }

    [Test]
    public void NoDepth_AddsNoVolatileBlock()
    {
        LLMSystemPromptBuilder.BuildVolatileAdditions(Context(null)).ShouldBeNull();
    }

    [Test]
    public async Task StablePrompt_CarriesTheOfferFirstRule_IdenticallyForEveryDepth()
    {
        var context = Context(new ExplainDepthProfile(ExplainExperienceLevel.Beginner, false));
        var beginner = await _builder.BuildSystemPromptAsync(context);
        context.ExplainDepth = new ExplainDepthProfile(ExplainExperienceLevel.Advanced, true);
        var advanced = await _builder.BuildSystemPromptAsync(context);
        context.ExplainDepth = null;
        var none = await _builder.BuildSystemPromptAsync(context);

        beginner.ShouldContain(ExplainDepthPromptTexts.OfferFirstGuide);
        advanced.ShouldBe(beginner);
        none.ShouldBe(beginner);
        beginner.ShouldNotContain(ExplainDepthPromptTexts.Header);
    }

    [Test]
    public async Task StablePrompt_ForbidsInventedConsequences()
    {
        var prompt = await _builder.BuildSystemPromptAsync(Context(new ExplainDepthProfile(ExplainExperienceLevel.Advanced, false)));

        prompt.ShouldContain("Never infer or add further consequences");
        prompt.ShouldContain("say plainly that you cannot confirm it");
    }
}
