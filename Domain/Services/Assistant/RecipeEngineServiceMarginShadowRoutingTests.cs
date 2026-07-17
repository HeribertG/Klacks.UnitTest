// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies the shadow-mode margin signal is a pure observer inside RecipeEngineService: on a
/// keyword-trigger match the plan's NeedsConfirmation follows ONLY the legacy CompetingSkillIntentDetector,
/// regardless of what the margin evaluator returns (a divergent gate verdict) or whether it throws. The
/// routing decision must be identical to the pre-shadow behaviour.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceMarginShadowRoutingTests
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

    private static RecipeSkillMarginResult GateVerdict(bool wouldGate) =>
        new("create_shift", 0.2, "list_absence_types", 0.7,
            Margin: 0.2 - 0.7, WouldGateAtPlaceholderThreshold: wouldGate,
            PermissionScope: "user-rights-scoped", ServedSkillsNotScored: 0, OldDetectorDecision: false);

    [Test]
    public async Task LegacyDetectorFindsNoCompetitor_ShadowWouldGate_PlanStillDoesNotGate()
    {
        _competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        _marginEvaluator.EvaluateAndLogAsync(Arg.Any<RecipeSkillMarginRequest>(), Arg.Any<CancellationToken>())
            .Returns(GateVerdict(wouldGate: true));

        var plan = await _service.ResolveAsync(Message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("create-shift-order");
        plan.NeedsConfirmation.ShouldBeFalse();
        await _marginEvaluator.Received(1)
            .EvaluateAndLogAsync(Arg.Any<RecipeSkillMarginRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LegacyDetectorFindsCompetitor_ShadowWouldNotGate_PlanStillGates()
    {
        _competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(new[] { "list_absence_types" });
        _marginEvaluator.EvaluateAndLogAsync(Arg.Any<RecipeSkillMarginRequest>(), Arg.Any<CancellationToken>())
            .Returns(GateVerdict(wouldGate: false));

        var plan = await _service.ResolveAsync(Message);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task ShadowEvaluatorThrows_RoutingUnaffected()
    {
        _competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        _marginEvaluator.EvaluateAndLogAsync(Arg.Any<RecipeSkillMarginRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<RecipeSkillMarginResult?>>(_ => throw new InvalidOperationException("boom"));

        var plan = await _service.ResolveAsync(Message);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeFalse();
    }

    [Test]
    public async Task ShadowReceivesRawMessageAndServedSkills()
    {
        _competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        RecipeSkillMarginRequest? captured = null;
        _marginEvaluator.EvaluateAndLogAsync(Arg.Do<RecipeSkillMarginRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns((RecipeSkillMarginResult?)null);

        await _service.ResolveAsync(Message, language: "de", userRights: new[] { "Admin" });

        captured.ShouldNotBeNull();
        captured!.Message.ShouldBe(Message);
        captured.ServedSkillNames.ShouldContain("create_shift");
        captured.UserRights.ShouldNotBeNull();
    }
}
