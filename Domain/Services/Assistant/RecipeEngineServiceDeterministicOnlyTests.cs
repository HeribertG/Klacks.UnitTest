// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The G5 probe calls GuaranteedSkillNamesAsync with allowSemanticFallback: false and discards the
/// competing-skill-intent verdict entirely - FindMatchingRecipeAsync must not compute it in the first
/// place. Without this, a deterministic-only probe call on a keyword-trigger match would still run
/// ICompetingSkillIntentDetector and the shadow-mode margin evaluator (embedding, reranking, and a
/// calibration log write) purely to produce a HasCompetingSkillIntent flag nobody reads.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceDeterministicOnlyTests
{
    private const string Message = "Erstelle einen neuen Auftrag";

    private static readonly AgentRecipe OrderRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "create-shift-order",
        Goal = "Create a shift order.",
        TriggerJson = """{"allOf":[{"anyWordStart":["erstell"]},{"anySubstring":["auftrag"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"create_shift"}]""",
        IsEnabled = true,
    };

    private IAgentRecipeRepository _recipeRepository = null!;
    private ICompetingSkillIntentDetector _competingDetector = null!;
    private IRecipeSkillMarginEvaluator _marginEvaluator = null!;
    private RecipeEngineService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OrderRecipe });

        _competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        _marginEvaluator = Substitute.For<IRecipeSkillMarginEvaluator>();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(_competingDetector);
        provider.GetService(typeof(IRecipeSkillMarginEvaluator)).Returns(_marginEvaluator);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(),
            Substitute.For<ILogger<RecipeEngineService>>());
    }

    [Test]
    public async Task DeterministicOnlyCall_OnAKeywordTriggerMatch_NeverInvokesTheCompetingIntentDetector()
    {
        var skills = await _service.GuaranteedSkillNamesAsync(
            userId: null, conversationId: null, Message, language: null, userRights: null,
            allowSemanticFallback: false, CancellationToken.None);

        skills.ShouldContain("create_shift");
        await _competingDetector.DidNotReceiveWithAnyArgs()
            .FindCompetingSkillNamesAsync(default!, default, default!, default, default!, default);
        await _marginEvaluator.DidNotReceiveWithAnyArgs()
            .EvaluateAndLogAsync(default!, default);
    }

    [Test]
    public async Task FullCall_OnTheSameKeywordTriggerMatch_StillInvokesTheCompetingIntentDetector()
    {
        _competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());

        await _service.GuaranteedSkillNamesAsync(userId: null, conversationId: null, Message);

        await _competingDetector.Received(1).FindCompetingSkillNamesAsync(
            Message, Arg.Any<string?>(), Arg.Any<RecipeTrigger>(), Arg.Any<IReadOnlyCollection<string>?>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }
}
