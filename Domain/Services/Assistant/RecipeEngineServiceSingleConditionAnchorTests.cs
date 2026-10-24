// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The semantic fallback for recipes whose trigger has a single phrase-list condition, run against the real
/// recipe-seeds.json including the real steps. Live 2026-10-20: "Suche selber radom die Mitarbeiter" ranked into
/// setup-planning-profile, which writes a planning-profile draft, and the user was walked into it after a "ja". That
/// recipe is now marked packAnchorRequired and needs a pack anchor in the message; the read-only setup-consultation
/// keeps the unrestricted semantic path its paraphrases depend on.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceSingleConditionAnchorTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string German = "de";
    private const string Spanish = "es";
    private const string PlanningProfileRecipe = "setup-planning-profile";
    private const string ConsultationRecipe = "setup-consultation";
    private const string SpanishPlanningProfileAnchor = "perfil de planificación";
    private const double StrongScore = 0.85;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<AgentRecipe> _seededRecipes = null!;

    private IAgentRecipeRepository _recipeRepository = null!;
    private IKnowledgeRetrievalService _knowledge = null!;
    private RecipeEngineService _service = null!;

    [OneTimeSetUp]
    public void LoadSeededRecipes()
    {
        var file = RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, RecipeSeedsFileName]);
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(File.ReadAllText(file), JsonReadOptions);
        _seededRecipes = seed!.Recipes
            .OrderBy(r => r.SortOrder)
            .Select(r => new AgentRecipe
            {
                Id = Guid.NewGuid(),
                Name = r.Name,
                Goal = r.Goal,
                TriggerJson = JsonSerializer.Serialize(r.Trigger),
                StepsJson = JsonSerializer.Serialize(r.Steps),
                IsEnabled = true,
                SortOrder = r.SortOrder
            })
            .ToList();
    }

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _knowledge = Substitute.For<IKnowledgeRetrievalService>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        var skillRepositoryStub = SkillRepositoryStubs.WithoutRequirements();
        provider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepositoryStub);
        provider.GetService(typeof(IRecipeCandidateRetriever)).Returns(new KnowledgeIndexRecipeCandidateRetriever(_knowledge));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private void OnlyRecipe(string name, Dictionary<string, List<string>>? anchors = null)
    {
        var recipe = _seededRecipes.Single(r => r.Name == name);
        recipe.Anchors = anchors;
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns([recipe]);
    }

    private void RankStrongly(string message, string recipeName) =>
        _knowledge.RetrieveAsync(
                message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(
            [
                new RetrievalCandidate(
                    new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = recipeName, Text = "irrelevant" },
                    StrongScore)
            ]));

    [Test]
    [TestCase("Suche selber radom die Mitarbeiter")]
    [TestCase("Such dir einfach ein paar Leute aus")]
    public async Task MutatingSingleConditionRecipe_StronglyRanked_WithoutAnchor_DoesNotResolve(string message)
    {
        OnlyRecipe(PlanningProfileRecipe);
        RankStrongly(message, PlanningProfileRecipe);

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task MutatingSingleConditionRecipe_WithAPackAnchorInTheMessage_StaysACandidate()
    {
        const string message = "quiero configurar mi perfil de planificación";
        OnlyRecipe(PlanningProfileRecipe, new Dictionary<string, List<string>> { [Spanish] = [SpanishPlanningProfileAnchor] });
        RankStrongly(message, PlanningProfileRecipe);

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(PlanningProfileRecipe);
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task ReadOnlySingleConditionRecipe_KeepsTheUnrestrictedSemanticPath()
    {
        const string message = "Ich weiss nicht, womit ich beginnen soll.";
        OnlyRecipe(ConsultationRecipe);
        RankStrongly(message, ConsultationRecipe);

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(ConsultationRecipe);
    }
}
