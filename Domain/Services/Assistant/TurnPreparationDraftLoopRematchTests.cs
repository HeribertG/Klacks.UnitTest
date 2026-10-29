// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The fresh recipe match while a draft loop (order review, planning-profile setup) is open. Live 2026-10-28: the order
/// review asked "Beginn 07:00?", the user answered "Passt, übernimm alle Vorschläge, keine Spesen." and the semantic
/// fallback offered two unrelated group recipes, because "alle" anchors them and "keine" defeats the leading-affirmation
/// guard. While a loop is open the match is keyword-only: a semantic candidate is not even retrieved, an explicit trigger
/// phrase still starts its flow. The live message must also not fire any seeded recipe trigger, or the fix would not hold.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationDraftLoopRematchTests
{
    private const string ConversationId = "conv-draft-loop";
    private const string GroupRecipeName = "bulk-add-employees-to-group";
    private const string EmployeeRecipeName = "create-employee";
    private const string LoopAnswer = "Passt, übernimm alle Vorschläge, keine Spesen.";
    private const string KeywordRequest = "Erstelle einen neuen Mitarbeiter";
    private const string Language = "de";
    private const double StrongScore = 0.85;

    private static readonly string[] RecipeSeedPath = ["Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"];
    private static readonly string[] LanguagesPath = ["Klacks.Api", "Plugins", "Languages"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe GroupRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = GroupRecipeName,
        Goal = "Add several employees to a group.",
        TriggerJson = """{"allOf":[{"anyWordStart":["hinzufüg"]},{"anyWordStart":["alle"]},{"anySubstring":["gruppe"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"groupName","prompt":"Which group?"},{"kind":"mutate","skill":"bulk_add_clients_to_group"}]""",
        IsEnabled = true,
        SortOrder = 1
    };

    private static readonly AgentRecipe EmployeeRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = EmployeeRecipeName,
        Goal = "Create an employee.",
        TriggerJson = """{"allOf":[{"anyWordStart":["erstell"]},{"anySubstring":["mitarbeiter"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"name","prompt":"Which name?"},{"kind":"mutate","skill":"create_employee"}]""",
        IsEnabled = true,
        SortOrder = 2
    };

    private IKnowledgeRetrievalService _knowledge = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { GroupRecipe, EmployeeRecipe });

        var pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);

        _knowledge = Substitute.For<IKnowledgeRetrievalService>();
        _knowledge.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(
            [
                new RetrievalCandidate(
                    new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = GroupRecipeName, Text = "irrelevant" },
                    StrongScore)
            ]));

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepository = SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepository);
        scopedProvider.GetService(typeof(IRecipeCandidateRetriever)).Returns(new KnowledgeIndexRecipeCandidateRetriever(_knowledge));
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

        _service = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            recipeEngine,
            Substitute.For<IRecipeRunRecorder>(),
            new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            Substitute.For<IAssistantLastActionStore>(),
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());
    }

    [TearDown]
    public void ResetPluginEntries()
    {
        AffirmationDetector.Reset();
        DeclineDetector.Reset();
    }

    [Test]
    public async Task OpenDraftLoop_TheLoopAnswer_StartsNoRecipeBySimilarity()
    {
        var context = Context(LoopAnswer, draftLoopOpen: true);

        var plan = await Resolve(context);

        plan.ShouldBeNull();
        await _knowledge.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default, default);
    }

    [Test]
    public async Task OpenDraftLoop_AnExplicitTriggerPhrase_StillStartsThatFlow()
    {
        var context = Context(KeywordRequest, draftLoopOpen: true);

        var plan = await Resolve(context);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(EmployeeRecipeName);
    }

    [Test]
    public async Task WithoutADraftLoop_TheSameAnswer_StillReachesTheSemanticFallback()
    {
        var context = Context(LoopAnswer, draftLoopOpen: false);

        var plan = await Resolve(context);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(GroupRecipeName);
    }

    [Test]
    public void TheLiveLoopAnswer_FiresNoSeededRecipeTrigger()
    {
        var synonyms = LoadPackFile("recipe-synonyms.json");
        var vetoes = LoadPackFile("recipe-vetoes.json");
        var hits = LoadTriggers()
            .Where(entry => RecipeTriggerMatcher.Matches(
                entry.Value, synonyms.GetValueOrDefault(entry.Key), LoopAnswer, null, Language, vetoes.GetValueOrDefault(entry.Key)))
            .Select(entry => entry.Key)
            .ToList();

        hits.ShouldBeEmpty();
    }

    private static LLMContext Context(string message, bool draftLoopOpen) => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        Language = Language,
        DraftLoopOpen = draftLoopOpen,
        AvailableFunctions = new List<LLMFunction>()
    };

    private Task<RecipeExecutionPlan?> Resolve(LLMContext context) =>
        _service.ResolveOrResumeRecipeAsync(
            context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId,
            pendingConfirmationForced: false, CancellationToken.None);

    private static Dictionary<string, RecipeTrigger> LoadTriggers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(RecipeSeedPath)));
        var triggers = new Dictionary<string, RecipeTrigger>(StringComparer.Ordinal);
        foreach (var recipe in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            if (recipe.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object)
            {
                triggers[recipe.GetProperty("name").GetString()!] =
                    JsonSerializer.Deserialize<RecipeTrigger>(trigger.GetRawText(), JsonOptions)!;
            }
        }

        return triggers;
    }

    private static Dictionary<string, List<string>> LoadPackFile(string fileName)
    {
        var file = Path.Combine(RepositoryRootLocator.RequireDirectory(LanguagesPath), Language, fileName);
        return File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))!
            : new Dictionary<string, List<string>>();
    }
}
