using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NSubstitute;
using NUnit.Framework;
using FluentAssertions;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class LLMSystemPromptBuilderTests
{
    private LLMSystemPromptBuilder _builder = null!;
    private IPromptTranslationProvider _translationProvider = null!;

    private static readonly Dictionary<string, string> GermanTranslations = new()
    {
        ["Intro"] = "Du bist ein hilfreicher KI-Assistent.",
        ["ToolUsageRules"] = "WICHTIGE REGELN.",
        ["SettingsNoPermission"] = "Kein Zugriff auf Einstellungen.",
        ["SettingsViewOnly"] = "Nur Lesezugriff.",
        ["HeaderUserContext"] = "Benutzer-Kontext",
        ["LabelUserId"] = "User ID",
        ["LabelPermissions"] = "Berechtigungen",
        ["HeaderAvailableFunctions"] = "Verfuegbare Funktionen"
    };

    [SetUp]
    public void Setup()
    {
        _translationProvider = Substitute.For<IPromptTranslationProvider>();
        _translationProvider.GetTranslationsAsync("de").Returns(GermanTranslations);
        _builder = new LLMSystemPromptBuilder(_translationProvider);
    }

    private static LLMContext CreateContext()
    {
        return new LLMContext
        {
            UserId = "user-123",
            UserRights = new List<string> { "CanViewSettings", "CanEditSettings" },
            AvailableFunctions = new List<LLMFunction>
            {
                new() { Name = "test_func", Description = "A test function" }
            },
            Language = "de"
        };
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithoutSoulPrompt_ReturnsBasePrompt()
    {
        // Arrange
        var context = CreateContext();

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("Du bist ein hilfreicher KI-Assistent.");
        result.Should().Contain("Benutzer-Kontext:");
        result.Should().Contain("user-123");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithSoulPrompt_IncludesItAtStart()
    {
        // Arrange
        var context = CreateContext();
        var soulPrompt = "=== IDENTITY ===\n[IDENTITY]\nI am a planning assistant.\n================";

        // Act
        var result = await _builder.BuildSystemPromptAsync(context, soulPrompt);

        // Assert
        result.Should().StartWith("=== IDENTITY ===");
        result.Should().Contain("I am a planning assistant.");
    }

    [Test]
    public async Task BuildSystemPromptAsync_IncludesAvailableFunctions()
    {
        // Arrange
        var context = CreateContext();

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("Verfuegbare Funktionen:");
        result.Should().Contain("test_func: A test function");
    }

    [Test]
    public async Task BuildSystemPromptAsync_NoSettingsPermission_ShowsNoPermissionNote()
    {
        // Arrange
        var context = CreateContext();
        context.UserRights = new List<string>();

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("Kein Zugriff auf Einstellungen.");
    }

    [Test]
    public async Task BuildSystemPromptAsync_ViewOnlyPermission_ShowsViewOnlyNote()
    {
        // Arrange
        var context = CreateContext();
        context.UserRights = new List<string> { "CanViewSettings" };

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("Nur Lesezugriff.");
    }

    [Test]
    public async Task BuildSystemPromptAsync_FullPermission_NoRestrictionNote()
    {
        // Arrange
        var context = CreateContext();
        context.UserRights = new List<string> { "CanViewSettings", "CanEditSettings" };

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().NotContain("Kein Zugriff auf Einstellungen.");
        result.Should().NotContain("Nur Lesezugriff.");
    }

    [Test]
    public async Task BuildSystemPromptAsync_SoulPromptIsTrimmed()
    {
        // Arrange
        var context = CreateContext();
        var soulPrompt = "  \n  Some soul content  \n  ";

        // Act
        var result = await _builder.BuildSystemPromptAsync(context, soulPrompt);

        // Assert
        result.Should().StartWith("Some soul content");
    }

    [Test]
    public async Task BuildSystemPromptAsync_NullSoulPrompt_OmitsIdentitySection()
    {
        // Arrange
        var context = CreateContext();

        // Act
        var result = await _builder.BuildSystemPromptAsync(context, null);

        // Assert
        result.Should().NotContain("=== IDENTITY ===");
        result.Should().StartWith("Du bist ein hilfreicher KI-Assistent.");
    }

    [Test]
    public async Task BuildSystemPromptAsync_IncludesToolUsageRules()
    {
        // Arrange
        var context = CreateContext();

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("WICHTIGE REGELN.");
    }
}
