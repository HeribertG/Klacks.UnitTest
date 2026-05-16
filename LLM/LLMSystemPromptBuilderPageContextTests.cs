// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMSystemPromptBuilder focused on AssistantPageContext rendering
/// (S1 of the autonomy roadmap — "=== CURRENT VIEW ===" block).
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class LLMSystemPromptBuilderPageContextTests
{
    private const string CurrentViewMarker = "=== CURRENT VIEW ===";

    private IPromptTranslationProvider _translationProvider = null!;
    private LLMSystemPromptBuilder _builder = null!;

    [SetUp]
    public void Setup()
    {
        _translationProvider = Substitute.For<IPromptTranslationProvider>();
        _translationProvider.GetTranslationsAsync(Arg.Any<string>()).Returns(new Dictionary<string, string>
        {
            { "Intro", "intro" },
            { "ToolUsageRules", "rules" },
            { "HeaderUserContext", "User Context" },
            { "LabelUserId", "UserId" },
            { "LabelPermissions", "Permissions" },
            { "SettingsNoPermission", "no settings" },
            { "SettingsViewOnly", "view only" }
        });
        _builder = new LLMSystemPromptBuilder(_translationProvider);
    }

    private static LLMContext CreateContext(AssistantPageContext? pageContext)
    {
        return new LLMContext
        {
            UserId = "user-1",
            UserRights = new List<string> { "CanViewSettings", "CanEditSettings" },
            AvailableFunctions = new List<LLMFunction>(),
            Language = "en",
            PageContext = pageContext
        };
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithoutPageContext_DoesNotRenderCurrentView()
    {
        var context = CreateContext(null);

        var result = await _builder.BuildSystemPromptAsync(context);

        Assert.That(result, Does.Not.Contain(CurrentViewMarker));
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithEmptyPageContext_DoesNotRenderCurrentView()
    {
        var context = CreateContext(new AssistantPageContext());

        var result = await _builder.BuildSystemPromptAsync(context);

        Assert.That(result, Does.Not.Contain(CurrentViewMarker));
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithRouteOnly_RendersRouteLine()
    {
        var context = CreateContext(new AssistantPageContext { CurrentRoute = "/schedule" });

        var result = await _builder.BuildSystemPromptAsync(context);

        Assert.That(result, Does.Contain(CurrentViewMarker));
        Assert.That(result, Does.Contain("route: /schedule"));
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithAllFields_RendersAllLines()
    {
        var pc = new AssistantPageContext
        {
            CurrentRoute = "/schedule",
            SelectedGroupId = "11111111-1111-1111-1111-111111111111",
            SelectedPeriodFrom = "2026-06-01",
            SelectedPeriodUntil = "2026-06-30",
            SelectedClientId = "22222222-2222-2222-2222-222222222222"
        };
        var context = CreateContext(pc);

        var result = await _builder.BuildSystemPromptAsync(context);

        Assert.That(result, Does.Contain("route: /schedule"));
        Assert.That(result, Does.Contain("selectedGroupId: 11111111-1111-1111-1111-111111111111"));
        Assert.That(result, Does.Contain("selectedPeriodFrom: 2026-06-01"));
        Assert.That(result, Does.Contain("selectedPeriodUntil: 2026-06-30"));
        Assert.That(result, Does.Contain("selectedClientId: 22222222-2222-2222-2222-222222222222"));
    }
}
