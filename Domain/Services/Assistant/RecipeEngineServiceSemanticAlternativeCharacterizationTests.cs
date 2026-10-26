// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the candidate walk in RecipeEngineService's semantic fallback, on the branches the existing
/// fixtures leave open: a candidate whose name resolves to no enabled recipe is passed over for the top match, a runner-up
/// with the top's own name is passed over, an unresolvable runner-up is surfaced by its bare name, a runner-up the user may
/// not run is not surfaced, the runner-up's goal translations travel with it, and the search asks for the top three.
/// The retriever port is substituted directly; recipes and message are those the semantic-fallback fixture already proves
/// to pass the question, veto and anchor gates.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceSemanticAlternativeCharacterizationTests
{
    private const string Message = "Etwas Neues anlegen.";
    private const string OnboardName = "onboard-employee";
    private const string GroupName = "create-group";
    private const string GroupGoal = "Create a new group.";
    private const string GroupGoalGerman = "Eine neue Gruppe anlegen";

    private static readonly AgentRecipe OnboardRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = OnboardName,
        Goal = "Onboard a new employee end to end.",
        TriggerJson = """{"allOf":[{"anyWordStart":["onboard","einstell"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"create_employee"}]""",
        IsEnabled = true,
    };

    private static readonly AgentRecipe GroupRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = GroupName,
        Goal = GroupGoal,
        GoalTranslations = new Dictionary<string, string> { ["de"] = GroupGoalGerman },
        TriggerJson = """{"allOf":[{"anyWordStart":["gruppe"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"create_group"}]""",
        IsEnabled = true,
    };

    private IRecipeCandidateRetriever _retriever = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private RecipeEngineService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe, GroupRecipe });

        _retriever = Substitute.For<IRecipeCandidateRetriever>();

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _skillRepository.GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Requirements((IReadOnlyCollection<string>)call[0]));

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        provider.GetService(typeof(IRecipeCandidateRetriever)).Returns(_retriever);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private static Dictionary<string, string?> Requirements(IReadOnlyCollection<string> names)
    {
        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["create_employee"] = Permissions.CanCreateClients,
            ["create_group"] = Permissions.CanEditSettings
        };

        return names.Where(all.ContainsKey).ToDictionary(name => name, name => all[name], StringComparer.OrdinalIgnoreCase);
    }

    private void GivenCandidates(params RecipeCandidate[] candidates)
    {
        _retriever.RetrieveAsync(Message, Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns((IReadOnlyList<RecipeCandidate>)candidates);
    }

    [Test]
    public async Task ATopCandidateWithoutAnEnabledRecipe_IsPassedOver_AndTheNextCandidateWins()
    {
        GivenCandidates(
            new RecipeCandidate("ghost-recipe", 0.95),
            new RecipeCandidate(OnboardName, 0.72),
            new RecipeCandidate(GroupName, 0.70));

        var plan = await _service.ResolveAsync(Message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OnboardName);
        plan.MatchedByMeaning.ShouldBeTrue();
        plan.AlternativeGoal.ShouldBe(GroupGoal);
    }

    [Test]
    public async Task ARunnerUpWithoutAnEnabledRecipe_IsSurfacedByItsBareName()
    {
        GivenCandidates(
            new RecipeCandidate(OnboardName, 0.72),
            new RecipeCandidate("ghost-recipe", 0.70));

        var plan = await _service.ResolveAsync(Message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OnboardName);
        plan.AlternativeGoal.ShouldBe("ghost-recipe");
        plan.AlternativeGoalTranslations.ShouldBeNull();
    }

    [Test]
    public async Task ARunnerUpNamingTheTopRecipe_IsPassedOver_ForTheNextDistinctRecipe()
    {
        GivenCandidates(
            new RecipeCandidate(OnboardName, 0.72),
            new RecipeCandidate("ONBOARD-EMPLOYEE", 0.71),
            new RecipeCandidate(GroupName, 0.70));

        var plan = await _service.ResolveAsync(Message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OnboardName);
        plan.AlternativeGoal.ShouldBe(GroupGoal);
    }

    [Test]
    public async Task TheRunnerUpsGoalTranslations_TravelWithIt()
    {
        GivenCandidates(
            new RecipeCandidate(OnboardName, 0.72),
            new RecipeCandidate(GroupName, 0.70));

        var plan = await _service.ResolveAsync(Message);

        plan.ShouldNotBeNull();
        plan!.AlternativeGoalTranslations.ShouldNotBeNull();
        plan.AlternativeGoalTranslations!["de"].ShouldBe(GroupGoalGerman);
    }

    [Test]
    public async Task ARunnerUpTheUserMayNotRun_IsNotSurfaced_AndIsNoDenial()
    {
        GivenCandidates(
            new RecipeCandidate(OnboardName, 0.72),
            new RecipeCandidate(GroupName, 0.70));

        var resolution = await _service.ResolveWithDenialAsync(
            Message, null, new[] { Permissions.CanCreateClients }, CancellationToken.None);

        resolution.Plan.ShouldNotBeNull();
        resolution.Plan!.Name.ShouldBe(OnboardName);
        resolution.Plan.AlternativeGoal.ShouldBeNull();
        resolution.Denial.ShouldBeNull();
    }

    [Test]
    public async Task ARunnerUpWithinTheUsersRights_IsSurfaced()
    {
        GivenCandidates(
            new RecipeCandidate(OnboardName, 0.72),
            new RecipeCandidate(GroupName, 0.70));

        var resolution = await _service.ResolveWithDenialAsync(
            Message, null, new[] { Permissions.CanCreateClients, Permissions.CanEditSettings }, CancellationToken.None);

        resolution.Plan.ShouldNotBeNull();
        resolution.Plan!.AlternativeGoal.ShouldBe(GroupGoal);
    }

    [Test]
    public async Task TheSemanticSearch_AsksForTheTopThreeRecipes()
    {
        GivenCandidates(new RecipeCandidate(OnboardName, 0.72));

        await _service.ResolveAsync(Message);

        await _retriever.Received(1).RetrieveAsync(Message, 3, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyCollection<string>?>());
    }
}
