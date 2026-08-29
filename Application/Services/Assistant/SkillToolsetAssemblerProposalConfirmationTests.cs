// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies the read side of the proposal/apply pairing in SkillToolsetAssembler: while a proposal hint
/// is outstanding, the paired apply skill is in the toolset regardless of how the user phrases the reply
/// — a bare "ja", but equally the bare answer to a follow-up question the model asked, such as a date.
/// Only an explicit refusal drops it; a missing hint, a discarded hint or a merely gate-held confirmation
/// (the other purpose of the same store) never produce it. The refusal signal comes from the shared
/// multilingual detector, so no new word list is involved in any language.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerProposalConfirmationTests
{
    private const string AlwaysOnSkillName = "navigate_to";
    private const string ApplySkillName = "apply_grouping";
    private const string GatedSkillName = "delete_group";
    private const string Affirmation = "Ja";
    private const string Refusal = "Nein, lass es";
    private const string UnrelatedMessage = "Zeig mir die Auswertung";

    // The reported live failure: the model asked "from which date should the memberships start?" and the
    // user answered with nothing but the date. It contains no word token at all, so it is neither an
    // affirmation nor a refusal.
    private const string BareDateAnswer = "2026-06-01";

    private static readonly TimeSpan ProposalHintWindow =
        TimeSpan.FromSeconds(AutonomyDefaults.ProposalHintWindowSeconds);

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IRetrievalQueryBuilder _retrievalQueryBuilder = null!;
    private ISkillRetrievalExpander _expander = null!;
    private IPendingUserNoteRepository _pendingUserNoteRepository = null!;
    private RecipeEngineService _recipeEngine = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private Agent _agent = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _confirmationStore = PendingStoreTestFactory.CreateConfirmationStore();

        _skillCache = Substitute.For<ISkillCacheService>();
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([]));

        _retrievalQueryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        _retrievalQueryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<string>(0));

        _expander = Substitute.For<ISkillRetrievalExpander>();
        _expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        _pendingUserNoteRepository = Substitute.For<IPendingUserNoteRepository>();
        _pendingUserNoteRepository.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);

        _recipeEngine = CreateRecipeEngine();

        _agent = new Agent { Id = Guid.NewGuid() };
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(AlwaysOnSkillName, alwaysOn: true),
                CreateSkill(ApplySkillName)
            });
    }

    [Test]
    public async Task Affirmation_WithAnOutstandingHint_GuaranteesThePairedApplySkill()
    {
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        var result = await AssembleAsync(Affirmation);

        FunctionNames(result).ShouldContain(ApplySkillName);
    }

    [TestCase("Ja")]
    [TestCase("yes")]
    [TestCase("oui")]
    [TestCase("sì")]
    [TestCase("ok")]
    public async Task Affirmation_InAnyCoreLanguage_GuaranteesThePairedApplySkill(string message)
    {
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        var result = await AssembleAsync(message);

        FunctionNames(result).ShouldContain(ApplySkillName);
    }

    [Test]
    public async Task Affirmation_WithoutAnyHint_DoesNotGuaranteeTheApplySkill()
    {
        var result = await AssembleAsync(Affirmation);

        FunctionNames(result).ShouldNotContain(ApplySkillName);
    }

    [Test]
    public async Task Refusal_DoesNotGuaranteeTheApplySkill_AndDropsTheHint()
    {
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        var result = await AssembleAsync(Refusal);

        FunctionNames(result).ShouldNotContain(ApplySkillName);
        _confirmationStore.PeekLatestForUser(
            _userId, ProposalHintWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public async Task BareDateAnswer_WithAnOutstandingHint_GuaranteesThePairedApplySkill()
    {
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        var result = await AssembleAsync(BareDateAnswer);

        FunctionNames(result).ShouldContain(ApplySkillName);
    }

    [TestCase("2026-06-01")]
    [TestCase("ab dem 1.6.2026")]
    [TestCase("Employee")]
    [TestCase("Zürich")]
    public async Task AnyNonRefusingAnswer_WithAnOutstandingHint_GuaranteesThePairedApplySkill(string message)
    {
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        var result = await AssembleAsync(message);

        FunctionNames(result).ShouldContain(ApplySkillName);
    }

    // Deliberate widening: an "unrelated" message cannot be told apart from the answer to a follow-up
    // question the model just asked, and guessing wrong closes the toolset on exactly the turn that needs
    // it. Since apply_grouping is no longer gated at the default autonomy level, what holds the call back
    // is now the catalogue text alone ("only after the user has seen the preview AND confirms"), plus the
    // fact that re-applying an already-applied grouping is a no-op.
    [Test]
    public async Task UnrelatedMessage_WithAnOutstandingHint_StillGuaranteesTheApplySkill()
    {
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        var result = await AssembleAsync(UnrelatedMessage);

        FunctionNames(result).ShouldContain(ApplySkillName);
    }

    [Test]
    public async Task Affirmation_AfterTheHintWasDiscarded_DoesNotGuaranteeTheApplySkill()
    {
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);
        _confirmationStore.DiscardProposalHints(_userId);

        var result = await AssembleAsync(Affirmation);

        FunctionNames(result).ShouldNotContain(ApplySkillName);
    }

    [Test]
    public async Task Affirmation_WithOnlyAGateHeldConfirmation_DoesNotGuaranteeTheApplySkill()
    {
        _confirmationStore.Create(_userId, GatedSkillName, new Dictionary<string, object>());

        var result = await AssembleAsync(Affirmation);

        FunctionNames(result).ShouldNotContain(ApplySkillName);
    }

    [Test]
    public async Task Affirmation_WithAHintForANonPermittedSkill_IsANoOp()
    {
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(AlwaysOnSkillName, alwaysOn: true),
                CreateSkill(ApplySkillName, requiredPermission: "CanEditClients")
            });
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        var result = await AssembleAsync(Affirmation);

        FunctionNames(result).ShouldNotContain(ApplySkillName);
    }

    private Task<SkillToolsetResult> AssembleAsync(string message)
    {
        var assembler = new SkillToolsetAssembler(
            _skillCache, _retrieval, _retrievalQueryBuilder, _expander,
            _pendingUserNoteRepository, _recipeEngine, _confirmationStore,
            PendingStoreTestFactory.CreatePlanningProfileDraftStore(),
            NoLearnedPhrases(),
            Substitute.For<ILogger<SkillToolsetAssembler>>());

        return assembler.AssembleAsync(
            _agent, new List<string>(), message, conversationId: null, currentRoute: null,
            _userId.ToString(), language: "de");
    }

    private static List<string> FunctionNames(SkillToolsetResult result) =>
        result.Functions.Select(f => f.Name).ToList();

    private static AgentSkill CreateSkill(string name, bool alwaysOn = false, string? requiredPermission = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = $"{name} description",
        ParametersJson = "[]",
        TriggerKeywords = "[]",
        AlwaysOn = alwaysOn,
        RequiredPermission = requiredPermission
    };

    private static RecipeEngineService CreateRecipeEngine()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());

        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);

        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);

        return new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private static ISkillPhraseRepository NoLearnedPhrases()
    {
        var repository = Substitute.For<ISkillPhraseRepository>();
        repository.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());
        return repository;
    }
}
