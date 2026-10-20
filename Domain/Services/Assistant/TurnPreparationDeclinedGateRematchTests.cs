// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The re-match after a reply that did not clear a pending confirmation gate. Live 2026-10-20: the gate asked
/// whether to add employees from Bern to a group, the user answered "Suche selber radom die Mitarbeiter", and the
/// semantic fallback ranked that follow-up into an unrelated recipe. The re-match on such a reply is now
/// keyword-only: a semantic candidate is not even retrieved, while an explicit trigger phrase still starts its flow.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationDeclinedGateRematchTests
{
    private const string ConversationId = "conv-declined-gate";
    private const string PendingRecipeName = "fill-group";
    private const string OtherRecipeName = "create-employee";
    private const string RefiningReply = "Suche selber radom die Mitarbeiter";
    private const string KeywordReply = "Erstelle lieber einen neuen Mitarbeiter";
    private const double StrongScore = 0.85;

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe PendingRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = PendingRecipeName,
        Goal = "Fill a group.",
        TriggerJson = """{"allOf":[{"anyWordStart":["füll"]},{"anySubstring":["gruppe"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"groupName","prompt":"Which group?"},{"kind":"mutate","skill":"fill_group"}]""",
        IsEnabled = true,
        SortOrder = 1
    };

    private static readonly AgentRecipe OtherRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = OtherRecipeName,
        Goal = "Create an employee.",
        TriggerJson = """{"allOf":[{"anyWordStart":["erstell"]},{"anySubstring":["mitarbeiter"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"name","prompt":"Which name?"},{"kind":"mutate","skill":"create_employee"}]""",
        IsEnabled = true,
        SortOrder = 2
    };

    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IKnowledgeRetrievalService _knowledge = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { PendingRecipe, OtherRecipe });
        recipeRepository.GetByNameAsync(PendingRecipeName, Arg.Any<CancellationToken>()).Returns(PendingRecipe);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = PendingRecipeName,
            AwaitingConfirmation = true,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

        _knowledge = Substitute.For<IKnowledgeRetrievalService>();
        _knowledge.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult(
            [
                new RetrievalCandidate(
                    new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = OtherRecipeName, Text = "irrelevant" },
                    StrongScore)
            ]));

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepositoryStub = Klacks.UnitTest.TestHelpers.SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepositoryStub);
        scopedProvider.GetService(typeof(IKnowledgeRetrievalService)).Returns(_knowledge);
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

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
    public async Task ARefiningReply_DropsTheGate_AndStartsNoRecipeBySimilarity()
    {
        var context = Context(RefiningReply);

        var plan = await Resolve(context);

        plan.ShouldBeNull();
        context.RecipeConfirmationDeclined.ShouldBeTrue();
        await _knowledge.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default);
    }

    [Test]
    public async Task AReplyWithAnExplicitTriggerPhrase_StillStartsThatFlow()
    {
        var context = Context(KeywordReply);

        var plan = await Resolve(context);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OtherRecipeName);
    }

    [Test]
    public async Task WithoutAPendingGate_TheSameMessage_StillReachesTheSemanticFallback()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var context = Context(RefiningReply);

        var plan = await Resolve(context);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OtherRecipeName);
    }

    private static LLMContext Context(string message) => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        Language = "de",
        AvailableFunctions = new List<LLMFunction>()
    };

    private Task<RecipeExecutionPlan?> Resolve(LLMContext context) =>
        _service.ResolveOrResumeRecipeAsync(
            context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId,
            pendingConfirmationForced: false, CancellationToken.None);
}
