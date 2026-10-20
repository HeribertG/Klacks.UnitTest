// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the rights check RecipeEngineService runs on a matched recipe: a recipe is offered only when the user
/// holds the right of EVERY step skill (searching steps included, administrator bypass included), neither the keyword
/// trigger nor the semantic ranking offers a refused recipe, a refusal wins (no other recipe is offered in place of the
/// refused first match, not by a later keyword trigger, not by the semantic fallback, not by a lower-ranked semantic candidate),
/// the refused recipe comes back as the denial of the resolution, a semantic candidate that fails its veto or anchor is no
/// match and does not count as a refusal, the check costs one query per matched recipe and shares its answer with the toolset
/// guarantee through the per-request memo, and a probe without rights is not checked at all. The proposal text the keyword
/// path builds without a model call is covered here too.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceRightsGateTests
{
    private const string OnboardMessage = "Bitte onboard einen neuen Mitarbeiter";
    private const string SettingsReadSkill = "list_period_issues";

    private static readonly AgentRecipe OnboardRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "onboard-employee",
        Goal = "Onboard a new employee end to end.",
        GoalTranslations = new Dictionary<string, string> { ["de"] = "Einen neuen Mitarbeiter einstellen" },
        TriggerJson = """{"allOf":[{"anyWordStart":["onboard"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"name","prompt":"Name?"},{"kind":"mutate","skill":"create_employee"}]""",
        IsEnabled = true,
    };

    private static readonly AgentRecipe AbsenceRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "add-absence",
        Goal = "Add an absence.",
        TriggerJson = """{"allOf":[{"anyWordStart":["abwesenheit"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"add_break"}]""",
        IsEnabled = true,
    };

    private static readonly AgentRecipe SearchFirstRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "review-period",
        Goal = "Review the open period issues.",
        TriggerJson = """{"allOf":[{"anyWordStart":["periodenprüfung"]}],"noneOf":[]}""",
        StepsJson = $$"""[{"kind":"search","skill":"{{SettingsReadSkill}}"},{"kind":"mutate","skill":"add_break"}]""",
        IsEnabled = true,
    };

    private IAgentRecipeRepository _recipeRepository = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private RecipeEngineService _service = null!;

    private RecipeResolution _lastResolution = null!;

    private RecipeDenial? LastDenial => _lastResolution.Denial;

    private async Task<RecipeExecutionPlan?> ResolveAsync(
        string message, string? language, IReadOnlyCollection<string>? userRights, string? excludedRecipeName = null)
    {
        _lastResolution = await _service.ResolveWithDenialAsync(
            message, language, userRights, CancellationToken.None, excludedRecipeName);
        return _lastResolution.Plan;
    }

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe, AbsenceRecipe, SearchFirstRecipe });

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _skillRepository.GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Requirements((IReadOnlyCollection<string>)call[0]));

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([]));

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        provider.GetService(typeof(IKnowledgeRetrievalService)).Returns(_retrieval);

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
            ["add_break"] = Permissions.CanEditSchedule,
            [SettingsReadSkill] = $"{Permissions.CanEditSettings},{Permissions.CanViewSettings}"
        };

        return names.Where(all.ContainsKey).ToDictionary(name => name, name => all[name], StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] PlannerFloor = [Permissions.CanViewClients, Permissions.CanEditSchedule];

    [Test]
    public async Task Keyword_RecipeNeedingARightTheUserLacks_IsNotOffered_AndTheDenialNamesTheMissingRight()
    {
        var plan = await ResolveAsync(OnboardMessage, "de", PlannerFloor);

        plan.ShouldBeNull();
        LastDenial.ShouldNotBeNull();
        LastDenial!.RecipeName.ShouldBe("onboard-employee");
        LastDenial.MissingRights.ShouldBe([Permissions.CanCreateClients]);
    }

    [Test]
    public async Task Keyword_RecipeWithinTheUsersRights_IsOffered_AndLeavesNoDenial()
    {
        var plan = await ResolveAsync("Neue Abwesenheit eintragen", "de", PlannerFloor);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("add-absence");
        LastDenial.ShouldBeNull();
    }

    [Test]
    public async Task Administrator_PassesTheCheckForEveryRecipe()
    {
        var plan = await ResolveAsync(OnboardMessage, "de", new[] { Roles.Admin });

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
    }

    [Test]
    public async Task ASearchStepSkillTheUserMayNotCall_DeniesTheRecipeToo_NotOnlyItsWritingSteps()
    {
        var plan = await ResolveAsync("Periodenprüfung bitte", "de", PlannerFloor);

        plan.ShouldBeNull("the search step would drop out of the toolset and the recipe would hang on it");
        LastDenial!.MissingRights.ShouldBe([Permissions.CanEditSettings, Permissions.CanViewSettings], ignoreOrder: true);
    }

    [Test]
    public async Task NoRights_AProbe_IsNotChecked()
    {
        var plan = await ResolveAsync(OnboardMessage, "de", userRights: null);

        plan.ShouldNotBeNull();
        await _skillRepository.DidNotReceiveWithAnyArgs()
            .GetRequiredPermissionsByNameAsync(default!, default);
    }

    [Test]
    public async Task ADeniedKeywordRecipe_WinsOverALaterMatchingRecipe_AndNothingElseIsOffered()
    {
        var both = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "onboard-and-absence",
            Goal = "Also matches the onboard keyword.",
            TriggerJson = """{"allOf":[{"anyWordStart":["onboard"]}],"noneOf":[]}""",
            StepsJson = """[{"kind":"mutate","skill":"add_break"}]""",
            IsEnabled = true,
        };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe, both });

        var plan = await ResolveAsync(OnboardMessage, "de", PlannerFloor);

        plan.ShouldBeNull("the recipe the message points at is refused; another flow must not be offered in its place");
        LastDenial.ShouldNotBeNull().RecipeName.ShouldBe("onboard-employee");
    }

    [Test]
    public async Task ADeniedKeywordRecipe_NeverFallsBackToTheSemanticRanking()
    {
        _retrieval.RetrieveAsync(OnboardMessage, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([Candidate("add-absence", 0.90)]));

        var plan = await ResolveAsync(OnboardMessage, "de", PlannerFloor);

        plan.ShouldBeNull();
        LastDenial.ShouldNotBeNull();
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default);
    }

    [Test]
    public async Task Semantic_DeniedTopCandidate_WinsOverALowerRankedPermittedOne()
    {
        var message = "Ich muss jemanden neu ins System aufnehmen und alles dazu erledigen";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult(
            [
                Candidate("onboard-employee", 0.91),
                Candidate("add-absence", 0.60)
            ]));

        var plan = await ResolveAsync(message, "de", PlannerFloor);

        plan.ShouldBeNull("the best candidate is refused for rights; the lower-ranked one is a different request");
        LastDenial.ShouldNotBeNull().RecipeName.ShouldBe("onboard-employee");
    }

    [Test]
    public async Task Semantic_TopCandidateVetoedOrWithoutAnchor_IsNoMatch_SoAPermittedRunnerUpIsStillOffered()
    {
        var guarded = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "guarded",
            Goal = "A guarded recipe.",
            TriggerJson = """{"allOf":[{"anyWordStart":["gesperrt"]}],"noneOf":[{"anySubstring":["jemanden"]}]}""",
            StepsJson = """[{"kind":"mutate","skill":"create_employee"}]""",
            IsEnabled = true,
        };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { guarded, AbsenceRecipe });
        var message = "Ich muss jemanden neu ins System aufnehmen und alles dazu erledigen";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([Candidate("guarded", 0.91), Candidate("add-absence", 0.60)]));

        var plan = await ResolveAsync(message, "de", PlannerFloor);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("add-absence");
        LastDenial.ShouldBeNull("a vetoed candidate is no match and so no refusal either");
    }
    [Test]
    public async Task Semantic_OnlyCandidateDenied_ResolvesNothingAndReportsTheDenial()
    {
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([Candidate("onboard-employee", 0.82)]));

        var plan = await ResolveAsync(message, "de", PlannerFloor);

        plan.ShouldBeNull();
        LastDenial.ShouldNotBeNull().RecipeName.ShouldBe("onboard-employee");
    }

    [Test]
    public async Task TheToolsetGuaranteeAndThePlanShareOneRightsAnswer_WithOneQueryPerMatchedRecipe()
    {
        var guaranteed = await _service.GuaranteedSkillNamesAsync(null, null, OnboardMessage, "de", PlannerFloor);
        var plan = await ResolveAsync(OnboardMessage, "de", PlannerFloor);

        guaranteed.ShouldBeEmpty("a refused recipe must not pull its step skills into the toolset");
        plan.ShouldBeNull();
        await _skillRepository.Received(1)
            .GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheMatchMemoIsKeyedByTheRights_SoANewUserOfTheSameScopeNeverGetsTheAdministratorsMatch()
    {
        var asAdministrator = await ResolveAsync(OnboardMessage, "de", new[] { Roles.Admin });
        var asPlanner = await ResolveAsync(OnboardMessage, "de", PlannerFloor);
        var asAdministratorAgain = await ResolveAsync(OnboardMessage, "de", new[] { Roles.Admin });

        asAdministrator.ShouldNotBeNull();
        asPlanner.ShouldBeNull("the planner's rights differ, so the memo entry computed for the administrator must not serve it");
        LastDenial.ShouldBeNull("the third call is the administrator again");
        asAdministratorAgain.ShouldNotBeNull();
    }

    [Test]
    public async Task ARightsListInAnyOrder_IsTheSameMemoKey()
    {
        await ResolveAsync(OnboardMessage, "de", new[] { Permissions.CanViewClients, Permissions.CanEditSchedule });
        await ResolveAsync(OnboardMessage, "de", new[] { Permissions.CanEditSchedule, Permissions.CanViewClients });

        await _skillRepository.Received(1)
            .GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExcludedRecipe_IsNotMatchedAgain()
    {
        var plan = await ResolveAsync(OnboardMessage, "de", new[] { Roles.Admin }, "onboard-employee");

        plan.ShouldBeNull();
    }

    [Test]
    public async Task NoMatchAtAll_CostsNoRightsQuery()
    {
        var plan = await ResolveAsync("Wie spät ist es?", "de", PlannerFloor);

        plan.ShouldBeNull();
        await _skillRepository.DidNotReceiveWithAnyArgs().GetRequiredPermissionsByNameAsync(default!, default);
    }

    [Test]
    public async Task KeywordProposal_InALanguageWithAGoalTranslation_IsBuiltWithoutAModel_ChipIncluded()
    {
        var plan = await ResolveAsync(OnboardMessage, "de", new[] { Roles.Admin });

        var proposal = plan!.DeterministicConfirmationText("de");

        proposal.ShouldNotBeNull();
        proposal.ShouldContain("Einen neuen Mitarbeiter einstellen");
        proposal.ShouldContain(LlmRepliesFormat.BlockPrefix);
    }

    [TestCase("es")]
    [TestCase(null)]
    [TestCase("")]
    public async Task KeywordProposal_InALanguageWithoutAFrameOrTranslation_IsLeftToTheModel(string? language)
    {
        var plan = await ResolveAsync(OnboardMessage, "de", new[] { Roles.Admin });

        plan!.DeterministicConfirmationText(language).ShouldBeNull();
    }

    [Test]
    public async Task KeywordProposal_AfterAnAmbiguousReply_IsLeftToTheModel_BecauseItCarriesTheNoteForTheReask()
    {
        var plan = await ResolveAsync(OnboardMessage, "de", new[] { Roles.Admin });
        plan!.DeterministicConfirmationText("de").ShouldNotBeNull("precondition: the plain proposal is deterministic");

        plan.NoteAmbiguousReply("Ask for an unambiguous yes or no.");

        plan.DeterministicConfirmationText("de").ShouldBeNull();
        plan.ConfirmationInstruction.ShouldContain("Ask for an unambiguous yes or no.");
    }

    [Test]
    public async Task KeywordProposal_WithoutAnyGoalTranslation_IsLeftToTheModel()
    {
        var plan = await ResolveAsync("Neue Abwesenheit eintragen", "de", new[] { Roles.Admin });

        plan!.DeterministicConfirmationText("de").ShouldBeNull();
    }

    [Test]
    public async Task SemanticProposal_IsDeterministicLikeAKeywordProposal()
    {
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([Candidate("onboard-employee", 0.82)]));

        var plan = await ResolveAsync(message, "de", new[] { Roles.Admin });

        plan.ShouldNotBeNull();
        plan!.MatchedByMeaning.ShouldBeTrue();
        plan.DeterministicConfirmationText("de").ShouldNotBeNull();
    }

    private static RetrievalCandidate Candidate(string recipeName, double score) =>
        new(new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = recipeName, Text = "irrelevant" }, score);
}
