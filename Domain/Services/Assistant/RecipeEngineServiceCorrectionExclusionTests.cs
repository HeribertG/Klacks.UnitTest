// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Stage 2 of the ask-step correction guard: the recipe the user just corrected must not come back.
///
/// The exclusion is applied once, to the recipe list at the head of FindMatchingRecipeAsync, and these
/// tests exist to prove both matching paths inherit it. Excluding in only one of them would leave the
/// other free to hand back the aborted recipe: the trigger walk for a message that still contains its
/// vocabulary, the semantic fallback for one that does not. Re-matching the corrected recipe is the worst
/// available outcome, because the plan restarts at step 0 with every slot the user already supplied
/// discarded.
///
/// The memo tests are the other half. FindMatchingRecipeAsync caches on (message, language, excluded) and
/// caches NULL misses too, so an exclusion missing from the key would not merely be slow - the first call
/// would decide the answer for both.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceCorrectionExclusionTests
{
    private const string SingleName = "add-employee-to-group";
    private const string BulkName = "bulk-add-employees-to-group";
    private const string ClientNameSlot = "clientName";
    private const string ConversationId = "conv-exclusion";

    /// <summary>
    /// Sorts first, so without an exclusion it wins any message both recipes match. Its ask slot is an
    /// entity name feeding a capturing search, which is what makes a correction at this step detectable.
    /// </summary>
    private static readonly AgentRecipe SingleAdd = new()
    {
        Id = Guid.NewGuid(),
        Name = SingleName,
        Goal = "Add one employee to a group.",
        TriggerJson = """{"allOf":[{"anyWordStart":["mitarbeiter"]}],"noneOf":[]}""",
        StepsJson =
            """
            [{"kind":"ask","slot":"clientName","prompt":"Which employee?"},
             {"kind":"search","skill":"search_employees","inject":{"searchTerm":"$clientName"},"capture":"Array[].Id as clientId"},
             {"kind":"mutate","skill":"add_client_to_group","inject":{"clientId":"$clientId"}}]
            """,
        IsEnabled = true,
        SortOrder = 1
    };

    private static readonly AgentRecipe BulkAdd = new()
    {
        Id = Guid.NewGuid(),
        Name = BulkName,
        Goal = "Add many employees to a group.",
        TriggerJson = """{"allOf":[{"anyWordStart":["alle"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"propose_grouping"}]""",
        IsEnabled = true,
        SortOrder = 2
    };

    /// <summary>
    /// Matches both recipes and is long enough to clear the correction length floor, so it exercises the
    /// exclusion rather than the detector's substance gate.
    /// </summary>
    private const string CorrectionMessage =
        "Nein, nicht der einzelne Mitarbeiter, ich meine alle Mitarbeiter und externen Personen";

    /// <summary>
    /// Matches neither trigger, so the engine falls through to the semantic path. That is the only way to
    /// prove the exclusion reaches the embedding fallback and not just the keyword walk.
    /// </summary>
    private const string SemanticOnlyMessage = "Bitte die gesamte Belegschaft dieser Gruppe zuordnen";

    private static readonly Guid UserId = Guid.NewGuid();

    private IAgentRecipeRepository _recipeRepository = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private RecipeEngineService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { SingleAdd, BulkAdd });
        _recipeRepository.GetByNameAsync(SingleName, Arg.Any<CancellationToken>()).Returns(SingleAdd);
        _recipeRepository.GetByNameAsync(BulkName, Arg.Any<CancellationToken>()).Returns(BulkAdd);

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(IKnowledgeRetrievalService)).Returns(_retrieval);
        provider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());
    }

    private static RetrievalResult RecipeResults(params (string SourceId, double Score)[] candidates) =>
        new(candidates
            .Select(c => new RetrievalCandidate(
                new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = c.SourceId, Text = "irrelevant" },
                c.Score))
            .ToList());

    private void StubRetrieval(string message, RetrievalResult result) =>
        _retrieval.RetrieveAsync(
                message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(result);

    [Test]
    public async Task WithoutAnExclusion_TheFirstMatchingRecipeWins()
    {
        var plan = await _service.ResolveAsync(CorrectionMessage);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(SingleName, "the single recipe sorts first, so it wins any message both match");
    }

    [Test]
    public async Task Exclusion_RemovesTheCorrectedRecipe_FromTheTriggerPath()
    {
        var plan = await _service.ResolveAsync(
            CorrectionMessage, excludedRecipeName: SingleName);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(BulkName);
    }

    /// <summary>
    /// The path that is easy to miss. The message matches no keyword trigger, so the answer comes from the
    /// embedding ranking - and the top candidate is the recipe being excluded. Without the exclusion
    /// reaching this path the corrected recipe comes straight back.
    /// </summary>
    [Test]
    public async Task Exclusion_RemovesTheCorrectedRecipe_FromTheSemanticPath()
    {
        StubRetrieval(SemanticOnlyMessage, RecipeResults((SingleName, 0.82), (BulkName, 0.70)));

        var withoutExclusion = await _service.ResolveAsync(SemanticOnlyMessage);
        withoutExclusion.ShouldNotBeNull();
        withoutExclusion!.Name.ShouldBe(SingleName, "the stronger candidate wins when nothing is excluded");

        var withExclusion = await _service.ResolveAsync(
            SemanticOnlyMessage, excludedRecipeName: SingleName);
        withExclusion.ShouldNotBeNull();
        withExclusion!.Name.ShouldBe(BulkName, "the fallback must skip the excluded recipe and take the next eligible one");
    }

    [Test]
    public async Task Exclusion_LeavesNothingWhenTheOnlyCandidateWasExcluded()
    {
        StubRetrieval(SemanticOnlyMessage, RecipeResults((SingleName, 0.82)));

        var plan = await _service.ResolveAsync(SemanticOnlyMessage, excludedRecipeName: SingleName);

        plan.ShouldBeNull("returning the excluded recipe would restart it with every collected slot lost");
    }

    /// <summary>
    /// The memo caches NULL misses as well as hits, so a key without the exclusion does not merely cost a
    /// second embedding round - the first call decides the answer for both.
    /// </summary>
    [Test]
    public async Task TheMemo_DistinguishesACallWithExclusion_FromOneWithout()
    {
        var first = await _service.ResolveAsync(CorrectionMessage);
        var second = await _service.ResolveAsync(CorrectionMessage, excludedRecipeName: SingleName);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first!.Name.ShouldBe(SingleName);
        second!.Name.ShouldBe(BulkName, "a memo hit from the call without exclusion would return the wrong recipe");
    }

    [Test]
    public async Task TheMemo_ServesARepeatedCallWithTheSameExclusion()
    {
        var first = await _service.ResolveAsync(CorrectionMessage, excludedRecipeName: SingleName);
        var second = await _service.ResolveAsync(CorrectionMessage, excludedRecipeName: SingleName);

        first!.Name.ShouldBe(BulkName);
        second!.Name.ShouldBe(BulkName);
    }

    /// <summary>
    /// The half that makes R1 solvable: the toolset is assembled before LLMService runs, so the correction
    /// branch cannot widen it afterwards. If this still returned the paused recipe's skills, the turn would
    /// run a plan whose forced skill is not in the list, and the forcing spine would silently no-op.
    /// </summary>
    [Test]
    public async Task GuaranteedSkillNames_OnACorrection_GuaranteesTheCorrectedRecipesSkills()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = SingleName,
            StepIndex = 0,
            Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AwaitingConfirmation = false,
            CaptureRewindUsed = false,
            TriggerMessage = "Füge einen Mitarbeiter der Gruppe Zürich hinzu"
        });

        var skills = await _service.GuaranteedSkillNamesAsync(
            UserId.ToString(), ConversationId, CorrectionMessage);

        skills.ShouldContain("propose_grouping");
        skills.ShouldNotContain("search_employees",
            "the paused recipe was corrected, so its own step skills are the wrong guarantee for this turn");
    }

    /// <summary>
    /// The control for the test above: an ordinary reply at the same paused step keeps today's behaviour,
    /// so the correction path is what changed and not the pending-recipe shortcut in general.
    /// </summary>
    [Test]
    public async Task GuaranteedSkillNames_OnAnOrdinaryReply_KeepsThePausedRecipesSkills()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = SingleName,
            StepIndex = 0,
            Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AwaitingConfirmation = false,
            CaptureRewindUsed = false,
            TriggerMessage = "Füge einen Mitarbeiter der Gruppe Zürich hinzu"
        });

        var skills = await _service.GuaranteedSkillNamesAsync(
            UserId.ToString(), ConversationId, "Müller");

        skills.ShouldContain("search_employees");
        skills.ShouldNotContain("propose_grouping");
    }

    /// <summary>
    /// The engine's own recovery must win over the correction guard here too: after an ambiguous capture the
    /// plan sits on the same ask slot and asks for a more specific answer, which is negation-shaped by
    /// nature. Guaranteeing a different recipe's skills there would abandon a working rewind.
    /// </summary>
    [Test]
    public async Task GuaranteedSkillNames_WhileDisambiguatingACapture_KeepsThePausedRecipesSkills()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = SingleName,
            StepIndex = 0,
            Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AwaitingConfirmation = false,
            CaptureRewindUsed = true,
            TriggerMessage = "Füge einen Mitarbeiter der Gruppe Zürich hinzu"
        });

        var skills = await _service.GuaranteedSkillNamesAsync(
            UserId.ToString(), ConversationId, CorrectionMessage);

        skills.ShouldContain("search_employees");
        skills.ShouldNotContain("propose_grouping");
    }
}
