using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using NSubstitute;
using FluentAssertions;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class LLMSystemPromptBuilderGuidelinesTests
{
    private IPromptTranslationProvider _translationProvider = null!;
    private LLMSystemPromptBuilder _builder = null!;

    [SetUp]
    public void Setup()
    {
        _translationProvider = Substitute.For<IPromptTranslationProvider>();
        _builder = new LLMSystemPromptBuilder(_translationProvider);
    }

    private static LLMContext CreateContext(string language = "en")
    {
        return new LLMContext
        {
            UserId = "user-123",
            UserRights = new List<string> { "CanViewSettings", "CanEditSettings" },
            AvailableFunctions = new List<LLMFunction>
            {
                new() { Name = "test_func", Description = "A test function" }
            },
            Language = language
        };
    }

    private static Dictionary<string, string> CreateTranslations(string language = "en")
    {
        return new Dictionary<string, string>
        {
            { "Intro", "You are a helpful assistant." },
            { "ToolUsageRules", "Use tools when appropriate." },
            { "HeaderUserContext", "User Context" },
            { "LabelUserId", "User ID" },
            { "LabelPermissions", "Permissions" },
            { "HeaderAvailableFunctions", "Available Functions" },
            { "SettingsNoPermission", "No settings permission" },
            { "SettingsViewOnly", "View settings only" }
        };
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithContext_ContainsUserId()
    {
        // Arrange
        var context = CreateContext();
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("user-123");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithContext_ContainsPermissions()
    {
        // Arrange
        var context = CreateContext();
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("CanViewSettings");
        result.Should().Contain("CanEditSettings");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithContext_ContainsFunctionName()
    {
        // Arrange
        var context = CreateContext();
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("test_func");
        result.Should().Contain("A test function");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithSoulAndMemoryPrompt_ContainsIdentity()
    {
        // Arrange
        var context = CreateContext();
        var soulPrompt = "I am a helpful planning assistant.";
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context, soulPrompt);

        // Assert
        result.Should().Contain("helpful planning assistant");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithoutSoulPrompt_DoesNotContainIdentitySection()
    {
        // Arrange
        var context = CreateContext();
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context, null);

        // Assert
        result.Should().Contain("You are a helpful assistant.");
    }

    [Test]
    public async Task BuildSystemPromptAsync_UsesCorrectLanguage()
    {
        // Arrange
        var context = CreateContext("de");
        var germanTranslations = new Dictionary<string, string>
        {
            { "Intro", "Du bist ein hilfreicher Assistent." },
            { "ToolUsageRules", "Verwende Werkzeuge wenn angemessen." },
            { "HeaderUserContext", "Benutzerkontext" },
            { "LabelUserId", "Benutzer-ID" },
            { "LabelPermissions", "Berechtigungen" },
            { "HeaderAvailableFunctions", "Verfuegbare Funktionen" },
            { "SettingsNoPermission", "Keine Einstellungsberechtigung" },
            { "SettingsViewOnly", "Nur Einstellungen ansehen" }
        };
        _translationProvider.GetTranslationsAsync("de").Returns(germanTranslations);

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("Benutzerkontext");
        result.Should().Contain("Verfuegbare Funktionen");
    }

    [Test]
    public async Task BuildSystemPromptAsync_NoViewSettingsPermission_ContainsNoPermissionNote()
    {
        // Arrange
        var context = new LLMContext
        {
            UserId = "user-456",
            UserRights = new List<string> { "SomeOtherPermission" },
            AvailableFunctions = new List<LLMFunction>(),
            Language = "en"
        };
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("No settings permission");
    }

    [Test]
    public async Task BuildSystemPromptAsync_ViewOnlyPermission_ContainsViewOnlyNote()
    {
        // Arrange
        var context = new LLMContext
        {
            UserId = "user-789",
            UserRights = new List<string> { "CanViewSettings" },
            AvailableFunctions = new List<LLMFunction>(),
            Language = "en"
        };
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().Contain("View settings only");
    }

    [Test]
    public async Task BuildSystemPromptAsync_BothSettingsPermissions_NoSettingsNote()
    {
        // Arrange
        var context = CreateContext();
        _translationProvider.GetTranslationsAsync("en").Returns(CreateTranslations());

        // Act
        var result = await _builder.BuildSystemPromptAsync(context);

        // Assert
        result.Should().NotContain("No settings permission");
        result.Should().NotContain("View settings only");
    }
}
