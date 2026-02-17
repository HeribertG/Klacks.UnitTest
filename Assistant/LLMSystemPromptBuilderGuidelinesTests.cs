using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NSubstitute;
using NUnit.Framework;
using FluentAssertions;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class LLMSystemPromptBuilderGuidelinesTests
{
    private LLMSystemPromptBuilder _builder = null!;
    private IPromptTranslationProvider _translationProvider = null!;

    private static readonly Dictionary<string, string> GermanTranslations = new()
    {
        ["Intro"] = "Du bist ein hilfreicher KI-Assistent für dieses Planungs-System.\nAntworte immer in der Sprache des Benutzers.\nDu kannst auch allgemeine Wissensfragen beantworten, nicht nur Fragen zum System.",
        ["ToolUsageRules"] = "WICHTIGE REGELN FÜR TOOL-VERWENDUNG:\n- Wenn der Benutzer eine Aktion anfordert (erstellen, löschen, navigieren, anzeigen etc.), verwende IMMER die entsprechende Funktion als Tool-Call.\n- Beschreibe die Aktion nicht nur in Text – führe sie aus.\n- Bei Lösch-Anfragen: Rufe zuerst die passende list_* Funktion auf um die ID zu finden, dann verwende die delete_* Funktion mit der gefundenen ID. Führe BEIDE Schritte in einer Anfrage durch.\n- Bei Navigation: Verwende IMMER die navigate_to_page Funktion.\n- Gib NIEMALS eine Textantwort zurück wenn eine passende Funktion verfügbar ist.",
        ["SettingsNoPermission"] = "WICHTIG: Dieser Benutzer hat KEINE Berechtigung für Einstellungen.",
        ["SettingsViewOnly"] = "Dieser Benutzer kann Einstellungen einsehen, aber nicht ändern.",
        ["HeaderUserContext"] = "Benutzer-Kontext",
        ["LabelUserId"] = "User ID",
        ["LabelPermissions"] = "Berechtigungen",
        ["HeaderAvailableFunctions"] = "Verfügbare Funktionen",
        ["HeaderPersistentKnowledge"] = "Persistentes Wissen",
        ["HeaderGuidelines"] = "Richtlinien",
        ["DefaultGuidelinesFallback"] = "- Be polite and professional\n- Use available functions when users ask for them\n- Give clear and precise instructions\n- Always check permissions before executing functions\n- For missing permissions: explain that the user needs to contact an administrator\n- MANDATORY: Every address MUST be validated via validate_address before saving\n- If address validation fails, NEVER offer to save the incorrect address."
    };

    private static readonly Dictionary<string, string> EnglishTranslations = new()
    {
        ["Intro"] = "You are a helpful AI assistant for this planning system.\nAlways respond in the user's language.\nYou can also answer general knowledge questions, not only questions about the system.",
        ["ToolUsageRules"] = "IMPORTANT RULES FOR TOOL USAGE:\n- When the user requests an action, ALWAYS use the corresponding function as a tool call.\n- Do not just describe the action in text – execute it.\n- For delete requests: First call the matching list_* function to find the ID, then use the delete_* function.\n- For navigation: ALWAYS use the navigate_to_page function.\n- NEVER return a text response when a matching function is available.",
        ["SettingsNoPermission"] = "IMPORTANT: This user does NOT have permission for settings.",
        ["SettingsViewOnly"] = "This user can view settings but cannot modify them.",
        ["HeaderUserContext"] = "User Context",
        ["LabelUserId"] = "User ID",
        ["LabelPermissions"] = "Permissions",
        ["HeaderAvailableFunctions"] = "Available Functions",
        ["HeaderPersistentKnowledge"] = "Persistent Knowledge",
        ["HeaderGuidelines"] = "Guidelines",
        ["DefaultGuidelinesFallback"] = "- Be polite and professional\n- Use available functions when users ask for them\n- Give clear and precise instructions\n- Always check permissions before executing functions\n- For missing permissions: explain that the user needs to contact an administrator\n- MANDATORY: Every address MUST be validated via validate_address before saving\n- If address validation fails, NEVER offer to save the incorrect address."
    };

    private static readonly Dictionary<string, string> FrenchTranslations = new()
    {
        ["Intro"] = "Vous êtes un assistant IA utile pour ce système de planification.\nRépondez toujours dans la langue de l'utilisateur.\nVous pouvez aussi répondre à des questions de culture générale.",
        ["ToolUsageRules"] = "RÈGLES IMPORTANTES POUR L'UTILISATION DES OUTILS:\n- Utilisez toujours la fonction correspondante.",
        ["SettingsNoPermission"] = "IMPORTANT: Cet utilisateur n'a PAS la permission pour les paramètres.",
        ["SettingsViewOnly"] = "Cet utilisateur peut consulter les paramètres mais ne peut pas les modifier.",
        ["HeaderUserContext"] = "Contexte utilisateur",
        ["LabelUserId"] = "ID utilisateur",
        ["LabelPermissions"] = "Autorisations",
        ["HeaderAvailableFunctions"] = "Fonctions disponibles",
        ["HeaderPersistentKnowledge"] = "Connaissances persistantes",
        ["HeaderGuidelines"] = "Directives",
        ["DefaultGuidelinesFallback"] = "- Be polite and professional\n- Use available functions when users ask for them\n- Give clear and precise instructions\n- Always check permissions before executing functions\n- For missing permissions: explain that the user needs to contact an administrator"
    };

    private static readonly Dictionary<string, string> ItalianTranslations = new()
    {
        ["Intro"] = "Sei un assistente AI utile per questo sistema di pianificazione.\nRispondi sempre nella lingua dell'utente.\nPuoi anche rispondere a domande di cultura generale.",
        ["ToolUsageRules"] = "REGOLE IMPORTANTI PER L'USO DEGLI STRUMENTI:\n- Usa sempre la funzione corrispondente.",
        ["SettingsNoPermission"] = "IMPORTANTE: Questo utente NON ha il permesso per le impostazioni.",
        ["SettingsViewOnly"] = "Questo utente può visualizzare le impostazioni ma non può modificarle.",
        ["HeaderUserContext"] = "Contesto utente",
        ["LabelUserId"] = "ID utente",
        ["LabelPermissions"] = "Autorizzazioni",
        ["HeaderAvailableFunctions"] = "Funzioni disponibili",
        ["HeaderPersistentKnowledge"] = "Conoscenze persistenti",
        ["HeaderGuidelines"] = "Linee guida",
        ["DefaultGuidelinesFallback"] = "- Be polite and professional\n- Use available functions when users ask for them\n- Give clear and precise instructions\n- Always check permissions before executing functions\n- For missing permissions: explain that the user needs to contact an administrator"
    };

    [SetUp]
    public void Setup()
    {
        _translationProvider = Substitute.For<IPromptTranslationProvider>();
        _translationProvider.GetTranslationsAsync("de").Returns(GermanTranslations);
        _translationProvider.GetTranslationsAsync("en").Returns(EnglishTranslations);
        _translationProvider.GetTranslationsAsync("fr").Returns(FrenchTranslations);
        _translationProvider.GetTranslationsAsync("it").Returns(ItalianTranslations);

        _builder = new LLMSystemPromptBuilder(_translationProvider);
    }

    private static LLMContext CreateContext(string language = "de")
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

    [Test]
    public async Task BuildSystemPromptAsync_NullGuidelines_UsesFallbackDefaults()
    {
        var context = CreateContext("en");

        var result = await _builder.BuildSystemPromptAsync(context, null, null, null);

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Be polite and professional");
        result.Should().Contain("Use available functions when users ask for them");
        result.Should().Contain("Always check permissions before executing functions");
    }

    [Test]
    public async Task BuildSystemPromptAsync_EmptyGuidelines_UsesFallbackDefaults()
    {
        var context = CreateContext("en");

        var result = await _builder.BuildSystemPromptAsync(context, null, null, "   ");

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Be polite and professional");
    }

    [Test]
    public async Task BuildSystemPromptAsync_CustomGuidelines_UsesCustomContent()
    {
        var context = CreateContext("en");
        var customGuidelines = "- Always respond in bullet points\n- Never use emojis";

        var result = await _builder.BuildSystemPromptAsync(context, null, null, customGuidelines);

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Always respond in bullet points");
        result.Should().Contain("Never use emojis");
        result.Should().NotContain("Be polite and professional");
    }

    [Test]
    public async Task BuildSystemPromptAsync_German_UsesRichtlinienHeader()
    {
        var context = CreateContext("de");
        var guidelines = "- Eigene Richtlinie";

        var result = await _builder.BuildSystemPromptAsync(context, null, null, guidelines);

        result.Should().Contain("Richtlinien:");
        result.Should().Contain("Eigene Richtlinie");
    }

    [Test]
    public async Task BuildSystemPromptAsync_French_UsesDirectivesHeader()
    {
        var context = CreateContext("fr");
        var guidelines = "- Custom directive";

        var result = await _builder.BuildSystemPromptAsync(context, null, null, guidelines);

        result.Should().Contain("Directives:");
        result.Should().Contain("Custom directive");
    }

    [Test]
    public async Task BuildSystemPromptAsync_Italian_UsesLineeGuidaHeader()
    {
        var context = CreateContext("it");
        var guidelines = "- Regola personalizzata";

        var result = await _builder.BuildSystemPromptAsync(context, null, null, guidelines);

        result.Should().Contain("Linee guida:");
        result.Should().Contain("Regola personalizzata");
    }

    [Test]
    [TestCase("de", "Richtlinien")]
    [TestCase("en", "Guidelines")]
    [TestCase("fr", "Directives")]
    [TestCase("it", "Linee guida")]
    public async Task BuildSystemPromptAsync_AllLanguages_FallbackContainsDefaultRules(string language, string expectedHeader)
    {
        var context = CreateContext(language);

        var result = await _builder.BuildSystemPromptAsync(context, null, null, null);

        result.Should().Contain($"{expectedHeader}:");
        result.Should().Contain("Be polite and professional");
        result.Should().Contain("contact an administrator");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithSoulAndGuidelines_ContainsBoth()
    {
        var context = CreateContext("en");
        var soul = "I am a helpful planning assistant.";
        var guidelines = "- Custom rule 1\n- Custom rule 2";

        var result = await _builder.BuildSystemPromptAsync(context, soul, null, guidelines);

        result.Should().Contain("=== IDENTITY ===");
        result.Should().Contain("helpful planning assistant");
        result.Should().Contain("Guidelines:");
        result.Should().Contain("Custom rule 1");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithMemoriesAndGuidelines_ContainsBoth()
    {
        var context = CreateContext("en");
        var memories = new List<AiMemory>
        {
            new() { Category = "test", Key = "key1", Content = "value1", Importance = 5 }
        };
        var guidelines = "- Custom guideline";

        var result = await _builder.BuildSystemPromptAsync(context, null, memories, guidelines);

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Custom guideline");
        result.Should().Contain("Persistent Knowledge:");
        result.Should().Contain("key1: value1");
    }

    [Test]
    public async Task BuildSystemPromptAsync_WithAllSections_CorrectOrder()
    {
        var context = CreateContext("en");
        var soul = "I am the assistant.";
        var memories = new List<AiMemory>
        {
            new() { Category = "info", Key = "k", Content = "v", Importance = 5 }
        };
        var guidelines = "- My rule";

        var result = await _builder.BuildSystemPromptAsync(context, soul, memories, guidelines);

        var identityIndex = result.IndexOf("=== IDENTITY ===");
        var functionsIndex = result.IndexOf("Available Functions:");
        var guidelinesIndex = result.IndexOf("Guidelines:");
        var memoryIndex = result.IndexOf("Persistent Knowledge:");

        identityIndex.Should().BeLessThan(functionsIndex);
        functionsIndex.Should().BeLessThan(guidelinesIndex);
        guidelinesIndex.Should().BeLessThan(memoryIndex);
    }

    [Test]
    public async Task BuildSystemPromptAsync_CustomGuidelines_TrimsWhitespace()
    {
        var context = CreateContext("en");
        var guidelines = "  \n  - Trimmed rule  \n  ";

        var result = await _builder.BuildSystemPromptAsync(context, null, null, guidelines);

        result.Should().Contain("- Trimmed rule");
    }
}
