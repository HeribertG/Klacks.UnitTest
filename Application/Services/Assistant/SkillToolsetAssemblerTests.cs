// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies the shared SkillToolsetAssembler: permission filtering, the domain-skill ontology gate,
/// the co-required expansion into free budget, failure fallbacks, and — as the drift guard this
/// extraction exists for — that the streaming and non-streaming chat paths produce the identical
/// toolset and gate value for the same input.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerTests
{
    private const string AlwaysOnSkillName = "navigate_to";
    private const string RetrievedSkillName = "search_employees";
    private const string NeighbourSkillName = "add_client_to_group";
    private const string RestrictedSkillName = "manage_settings";
    private const string RequiredRight = "CanManageSettings";
    private const string MultiPermissionSkillName = "propose_grouping";
    private const string MultiPermissionRequirement = "CanEditClients,CanViewGroups";
    private const string UserMessage = "Bitte such mir die passenden Mitarbeitenden zusammen";
    private const string UserId = "user-1";

    // The skill the 2026-08-29 end-to-end runs watched stay outside the toolset over six generated
    // wordings while its neighbour got through every time. It is the canonical shape of a routing gap:
    // retrieval knows the skill, the provider cap drops it, and a learned wording used to change nothing.
    private const string LearnedSkillName = "set_client_availability";
    private const string SecondLearnedSkillName = "clear_client_availability";
    private const string ThirdLearnedSkillName = "get_export_formats_settings";
    private const string LongLearnedWording = "die passenden mitarbeitenden";
    private const string ShortLearnedWording = "such mir";
    private const string ThirdLearnedWording = "zusammen";

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IRetrievalQueryBuilder _retrievalQueryBuilder = null!;
    private ISkillRetrievalExpander _expander = null!;
    private IPendingUserNoteRepository _pendingUserNoteRepository = null!;
    private RecipeEngineService _recipeEngine = null!;
    private IPendingPlanningProfileDraftStore _planningProfileDraftStore = null!;
    private ISkillPhraseRepository _skillPhrases = null!;
    private Agent _agent = null!;

    [SetUp]
    public void Setup()
    {
        _skillCache = Substitute.For<ISkillCacheService>();
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
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

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        _recipeEngine = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());

        _planningProfileDraftStore = PendingStoreTestFactory.CreatePlanningProfileDraftStore();

        // Nothing learned is the state every installation starts in, so it is also the default here:
        // every assertion that predates the learned-phrase guarantee must stay exactly as true as before.
        _skillPhrases = Substitute.For<ISkillPhraseRepository>();
        _skillPhrases.GetActiveBySourceAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());

        _agent = new Agent { Id = Guid.NewGuid() };
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(AlwaysOnSkillName, alwaysOn: true),
                CreateSkill(RetrievedSkillName),
                CreateSkill(NeighbourSkillName),
                CreateSkill(RestrictedSkillName, requiredPermission: RequiredRight),
                CreateSkill(MultiPermissionSkillName, requiredPermission: MultiPermissionRequirement),
                CreateSkill("set_planning_profile_parameters"),
                CreateSkill("preview_planning_profile"),
                CreateSkill("apply_planning_profile"),
                CreateSkill("cancel_planning_profile_setup"),
                CreateSkill(LearnedSkillName, sortOrder: 900),
                CreateSkill(SecondLearnedSkillName, sortOrder: 910),
                CreateSkill(ThirdLearnedSkillName, sortOrder: 920)
            });

        SetupRetrievalResult(new RetrievalResult([]));
    }

    /// <summary>
    /// The turns that continue a planning-profile setup are bare answers — an industry name, a number.
    /// They carry no trigger keyword, the recipe that started the flow has already completed, and
    /// retrieval alone put mutating scheduling-rule skills ahead of the loop (measured 2026-08-10: the
    /// live run called create_scheduling_rule and never start_planning_profile_setup). The open draft is
    /// therefore the signal that keeps the loop reachable.
    /// </summary>
    [Test]
    public async Task OpenPlanningProfileDraft_GuaranteesLoopSkills_OnAKeywordFreeFollowUpTurn()
    {
        var userId = Guid.NewGuid();
        const string conversationId = "conv-planning-profile";
        _planningProfileDraftStore.Set(userId, conversationId, new PlanningProfileDraft());

        var result = await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), "Sicherheitsdienst", conversationId, currentRoute: null,
            userId.ToString(), language: "de");

        var names = FunctionNames(result);
        names.ShouldContain("set_planning_profile_parameters");
        names.ShouldContain("preview_planning_profile");
        names.ShouldContain("apply_planning_profile");
        names.ShouldContain("cancel_planning_profile_setup");
    }

    [Test]
    public async Task WithoutPlanningProfileDraft_LoopSkillsAreNotGuaranteed()
    {
        var result = await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), "Sicherheitsdienst", "conv-without-draft", currentRoute: null,
            Guid.NewGuid().ToString(), language: "de");

        FunctionNames(result).ShouldNotContain("set_planning_profile_parameters");
    }

    private static List<string> FunctionNames(SkillToolsetResult result) =>
        result.Functions.Select(f => f.Name).ToList();

    private SkillToolsetAssembler CreateAssembler()
    {
        return new SkillToolsetAssembler(
            _skillCache, _retrieval, _retrievalQueryBuilder, _expander,
            _pendingUserNoteRepository, _recipeEngine,
            PendingStoreTestFactory.CreateConfirmationStore(),
            _planningProfileDraftStore,
            _skillPhrases,
            Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

    private void SetupRetrievalResult(RetrievalResult result)
    {
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(result);
    }

    private static RetrievalResult RetrievalHit(params string[] skillNames)
    {
        var candidates = skillNames
            .Select(name => new RetrievalCandidate(
                new KnowledgeEntry
                {
                    Id = Guid.NewGuid(),
                    Kind = KnowledgeEntryKind.Skill,
                    SourceId = name,
                    Text = $"{name}. A skill."
                },
                0.9))
            .ToList();
        return new RetrievalResult(candidates);
    }

    private static AgentSkill CreateSkill(
        string name, bool alwaysOn = false, string? requiredPermission = null, int sortOrder = 0,
        string triggerKeywords = "[]")
    {
        return new AgentSkill
        {
            Name = name,
            Description = "A skill.",
            ParametersJson = "[]",
            AlwaysOn = alwaysOn,
            RequiredPermission = requiredPermission,
            SortOrder = sortOrder,
            TriggerKeywords = triggerKeywords
        };
    }

    private void GivenLearnedPhrases(params (string OwnerName, string Phrase, string OwnerKind)[] phrases)
    {
        _skillPhrases.GetActiveBySourceAsync(
                SkillPhraseSources.Learned, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(phrases
                .Select(p => new SkillPhrase
                {
                    OwnerKind = p.OwnerKind,
                    OwnerName = p.OwnerName,
                    Phrase = p.Phrase,
                    Language = "de",
                    Kind = SkillPhraseKinds.Synonym,
                    Source = SkillPhraseSources.Learned,
                    Status = SkillPhraseStatuses.Active
                })
                .ToList());
    }

    private void GivenLearnedSkillPhrase(string ownerName, string phrase) =>
        GivenLearnedPhrases((ownerName, phrase, SkillPhraseOwnerKinds.Skill));

    private Task<SkillToolsetResult> AssembleAsync(List<string>? userRights = null)
    {
        return CreateAssembler().AssembleAsync(
            _agent, userRights ?? new List<string>(), UserMessage,
            conversationId: null, currentRoute: null, UserId, language: null, cancellationToken: CancellationToken.None);
    }

    [Test]
    public async Task AssembleAsync_NullAgent_ReturnsEmptyToolsetWithoutDomainContext()
    {
        var result = await CreateAssembler().AssembleAsync(
            null, new List<string>(), UserMessage, null, null, UserId, null, cancellationToken: CancellationToken.None);

        result.Functions.ShouldBeEmpty();
        result.HasDomainSkillContext.ShouldBeFalse();
    }

    [Test]
    public async Task AssembleAsync_RetrievalEmpty_ReturnsAlwaysOnOnly_GateIsFalse()
    {
        var result = await AssembleAsync();

        result.Functions.Select(f => f.Name).ShouldBe(new[] { AlwaysOnSkillName });
        result.HasDomainSkillContext.ShouldBeFalse();
    }

    [Test]
    public async Task AssembleAsync_RetrievalHit_IncludesSkillAndGateIsTrue()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));

        var result = await AssembleAsync();

        result.Functions.ShouldContain(f => f.Name == RetrievedSkillName);
        result.Functions.ShouldContain(f => f.Name == AlwaysOnSkillName);
        result.HasDomainSkillContext.ShouldBeTrue();
    }

    // The candidate pool is 25 wide and shared: a recipe reaching it can only ever displace a skill,
    // because the toolset is built by matching candidates against skill names. The recipe pass has
    // filtered by kind since 2026-08-05; this is the other half of that change.
    [Test]
    public async Task AssembleAsync_AsksTheRetrievalForSkillsOnly()
    {
        await AssembleAsync();

        await _retrieval.Received(1).RetrieveAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), KnowledgeEntryKind.Skill);
    }

    [Test]
    public async Task AssembleAsync_RetrievalThrows_FallsBackToAlwaysOn_GateStaysTrue()
    {
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns<RetrievalResult>(_ => throw new InvalidOperationException("embedding backend down"));

        var result = await AssembleAsync();

        result.Functions.Select(f => f.Name).ShouldBe(new[] { AlwaysOnSkillName });
        result.HasDomainSkillContext.ShouldBeTrue();
    }

    [Test]
    public async Task AssembleAsync_RetrievedSkillWithoutPermission_IsExcluded()
    {
        SetupRetrievalResult(RetrievalHit(RestrictedSkillName));

        var result = await AssembleAsync();

        result.Functions.ShouldNotContain(f => f.Name == RestrictedSkillName);
    }

    [Test]
    public async Task AssembleAsync_CommaSeparatedPermission_UserHasAllRequiredRights_IsIncluded()
    {
        SetupRetrievalResult(RetrievalHit(MultiPermissionSkillName));
        var userRights = new List<string> { "CanEditClients", "CanViewGroups" };

        var result = await AssembleAsync(userRights);

        result.Functions.ShouldContain(f => f.Name == MultiPermissionSkillName);
    }

    [Test]
    public async Task AssembleAsync_CommaSeparatedPermission_UserHasOnlyOneRight_IsExcluded()
    {
        SetupRetrievalResult(RetrievalHit(MultiPermissionSkillName));
        var userRights = new List<string> { "CanEditClients" };

        var result = await AssembleAsync(userRights);

        result.Functions.ShouldNotContain(f => f.Name == MultiPermissionSkillName);
    }

    [Test]
    public async Task AssembleAsync_ExpanderNeighbour_IsAddedWithinFreeBudget()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        _expander.ExpandAsync(
                _agent.Id, Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new List<AgentSkill> { CreateSkill(NeighbourSkillName) });

        var result = await AssembleAsync();

        result.Functions.ShouldContain(f => f.Name == NeighbourSkillName);
    }

    [Test]
    public async Task AssembleAsync_ExpanderThrows_SelectionSurvivesWithoutExpansion()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        _expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AgentSkill>>(_ => throw new InvalidOperationException("relation store down"));

        var result = await AssembleAsync();

        result.Functions.ShouldContain(f => f.Name == RetrievedSkillName);
        result.HasDomainSkillContext.ShouldBeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StreamingAndNonStreamingPaths_SameInput_ProduceIdenticalToolsets(bool retrievalHits)
    {
        if (retrievalHits)
        {
            SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
            _expander.ExpandAsync(
                    _agent.Id, Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new List<AgentSkill> { CreateSkill(NeighbourSkillName) });
        }

        var assembler = CreateAssembler();
        var providerOrchestrator = new LLMProviderOrchestrator(
            Substitute.For<ILogger<LLMProviderOrchestrator>>(),
            Substitute.For<ILLMProviderFactory>(),
            Substitute.For<ILLMRepository>());
        var budgetPolicy = new ContextBudgetPolicy();

        LLMContext? streamingContext = null;
        var streamingLLMService = Substitute.For<ILLMService>();
        streamingLLMService.ProcessStreamAsync(
                Arg.Do<LLMContext>(c => streamingContext = c), Arg.Any<CancellationToken>())
            .Returns(EmptyStream());
        var orchestrator = new LLMStreamingOrchestrator(
            streamingLLMService, _skillCache, assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            providerOrchestrator,
            budgetPolicy,
            Substitute.For<ILogger<LLMStreamingOrchestrator>>());

        LLMContext? nonStreamingContext = null;
        var nonStreamingLLMService = Substitute.For<ILLMService>();
        nonStreamingLLMService.ProcessAsync(Arg.Do<LLMContext>(c => nonStreamingContext = c))
            .Returns(new LLMResponse());
        var handler = new ProcessLLMMessageCommandHandler(
            nonStreamingLLMService, Substitute.For<IAgentRepository>(), _skillCache, assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            providerOrchestrator,
            budgetPolicy);

        await foreach (var _ in orchestrator.ProcessStreamAsync(
            new LLMStreamRequest { Message = UserMessage, UserId = UserId }, CancellationToken.None))
        {
        }

        await handler.Handle(
            new ProcessLLMMessageCommand { Message = UserMessage, UserId = UserId }, CancellationToken.None);

        streamingContext.ShouldNotBeNull();
        nonStreamingContext.ShouldNotBeNull();
        nonStreamingContext!.AvailableFunctions.Select(f => f.Name)
            .ShouldBe(streamingContext!.AvailableFunctions.Select(f => f.Name));
        nonStreamingContext.HasDomainSkillContext.ShouldBe(streamingContext.HasDomainSkillContext);
    }

    private static async IAsyncEnumerable<SseChunk> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// The point of learning a wording. A learned row reaches the embedding text and nothing else, so
    /// before this guarantee existed the only thing a lesson could do was nudge a ranking that had
    /// already failed - which is why six generated wordings in a row left their skill outside the toolset.
    /// </summary>
    [Test]
    public async Task ALearnedWordingInTheMessage_PutsItsSkillInTheToolsetRetrievalNeverOffered()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        GivenLearnedSkillPhrase(LearnedSkillName, LongLearnedWording);

        var result = await AssembleAsync();

        FunctionNames(result).ShouldContain(LearnedSkillName);
    }

    /// <summary>
    /// Presence alone would prove nothing: an uncapped turn contains everything. The cap is set so that
    /// truncation really runs, and the learned skill carries the WORST sort order in the set - without a
    /// guarantee slot it would be the first thing dropped, not the last.
    /// </summary>
    [Test]
    public async Task ALearnedWordingsSkill_OutranksRetrievedSkillsAtTheProviderCap()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName, NeighbourSkillName, MultiPermissionSkillName));
        GivenLearnedSkillPhrase(LearnedSkillName, LongLearnedWording);
        var userRights = new List<string> { "CanEditClients", "CanViewGroups" };

        var result = await CreateAssembler().AssembleAsync(
            _agent, userRights, UserMessage, conversationId: null, currentRoute: null, UserId,
            language: null, maxToolsForProvider: 3, cancellationToken: CancellationToken.None);

        var names = FunctionNames(result);
        names.Count.ShouldBe(3);
        names.ShouldContain(AlwaysOnSkillName);
        names.ShouldContain(LearnedSkillName);
        names.Count(name => name is RetrievedSkillName or NeighbourSkillName or MultiPermissionSkillName)
            .ShouldBe(1, "Two retrieved skills had to give way to the always-on and the guaranteed one.");
    }

    [Test]
    public async Task ALearnedWordingThatDoesNotOccurInTheMessage_GuaranteesNothing()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        GivenLearnedSkillPhrase(LearnedSkillName, "verfuegbarkeit eines kunden festlegen");

        var result = await AssembleAsync();

        FunctionNames(result).ShouldNotContain(LearnedSkillName);
    }

    /// <summary>
    /// A capability writes its learned wording against the RECIPE name. That name is never a function, so
    /// a recipe-owned row could only ever burn a guarantee slot and resolve to nothing.
    /// </summary>
    [Test]
    public async Task ALearnedWordingOfARecipe_TakesNoSkillSlot()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        GivenLearnedPhrases((LearnedSkillName, LongLearnedWording, SkillPhraseOwnerKinds.Recipe));

        var result = await AssembleAsync();

        FunctionNames(result).ShouldNotContain(LearnedSkillName);
    }

    /// <summary>
    /// Guaranteed skills survive the cap ahead of retrieved ones, so an unbounded number of them would let
    /// the learning loop crowd out the retrieval it is meant to complement. The longest wording is the most
    /// specific one and therefore the one that keeps its slot.
    /// </summary>
    [Test]
    public async Task MoreMatchingWordingsThanTheCap_GuaranteeTheLongestOnesOnly()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        GivenLearnedPhrases(
            (LearnedSkillName, LongLearnedWording, SkillPhraseOwnerKinds.Skill),
            (SecondLearnedSkillName, ShortLearnedWording, SkillPhraseOwnerKinds.Skill),
            (ThirdLearnedSkillName, ThirdLearnedWording, SkillPhraseOwnerKinds.Skill));

        var result = await AssembleAsync();

        var names = FunctionNames(result);
        names.ShouldContain(LearnedSkillName);
        names.Count(name => name is LearnedSkillName or SecondLearnedSkillName or ThirdLearnedSkillName)
            .ShouldBe(LearnedPhraseMatcher.GuaranteeCap);
    }

    /// <summary>
    /// A wording this short matches by accident. The learning loop never produces one, but the admin card
    /// can rewrite a learned row to anything, and a two-letter row would otherwise guarantee its skill on
    /// almost every turn.
    /// </summary>
    [Test]
    public async Task ALearnedWordingShorterThanTheMatchFloor_GuaranteesNothing()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        GivenLearnedSkillPhrase(LearnedSkillName, "mi");

        var result = await AssembleAsync();

        FunctionNames(result).ShouldNotContain(LearnedSkillName);
    }

    [Test]
    public async Task AFailingPhraseLookup_LeavesTheRestOfTheToolsetIntact()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        _skillPhrases.GetActiveBySourceAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SkillPhrase>>(_ => throw new InvalidOperationException("phrase store down"));

        var result = await AssembleAsync();

        var names = FunctionNames(result);
        names.ShouldContain(RetrievedSkillName);
        names.ShouldContain(AlwaysOnSkillName);
        names.ShouldNotContain(LearnedSkillName);
    }

    /// <summary>
    /// The switch routing oracle O1 flips. PhraseLearner probes with the wording it has just written, so a
    /// guarantee keyed on that wording would report every generated phrase a success - including one that
    /// merely echoes the utterance and generalises to nothing.
    /// </summary>
    [Test]
    public async Task WithTheGuaranteeSwitchedOff_ALearnedWordingChangesNothing()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        GivenLearnedSkillPhrase(LearnedSkillName, LongLearnedWording);

        var result = await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), UserMessage, conversationId: null, currentRoute: null, UserId,
            language: null, applyLearnedPhraseGuarantee: false, cancellationToken: CancellationToken.None);

        FunctionNames(result).ShouldNotContain(LearnedSkillName);
    }

    [Test]
    public async Task WithTheGuaranteeSwitchedOff_ThePhraseStoreIsNotEvenRead()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));

        await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), UserMessage, conversationId: null, currentRoute: null, UserId,
            language: null, applyLearnedPhraseGuarantee: false, cancellationToken: CancellationToken.None);

        await _skillPhrases.DidNotReceive().GetActiveBySourceAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // W1.6: every function carries the reason why it is in the toolset, so the trajectory snapshot
    // can later answer "which source won" per chosen skill.
    [Test]
    public async Task AlwaysOnSkill_IsMarkedAlwaysOnWithoutScore()
    {
        var result = await AssembleAsync();

        var function = result.Functions.Single(f => f.Name == AlwaysOnSkillName);
        function.ToolsetSource.ShouldBe(ToolsetSkillSource.AlwaysOn);
        function.RetrievalScore.ShouldBeNull();
    }

    [Test]
    public async Task RetrievedSkill_IsMarkedRetrievedWithItsRerankScore()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));

        var result = await AssembleAsync();

        var function = result.Functions.Single(f => f.Name == RetrievedSkillName);
        function.ToolsetSource.ShouldBe(ToolsetSkillSource.Retrieved);
        function.RetrievalScore.ShouldBe(0.9);
    }

    [Test]
    public async Task LearnedPhraseSkill_IsMarkedLearnedPhrase()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        GivenLearnedSkillPhrase(LearnedSkillName, LongLearnedWording);

        var result = await AssembleAsync();

        result.Functions.Single(f => f.Name == LearnedSkillName).ToolsetSource
            .ShouldBe(ToolsetSkillSource.LearnedPhrase);
    }

    [Test]
    public async Task KeywordMatchedSkill_IsMarkedKeyword()
    {
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(AlwaysOnSkillName, alwaysOn: true),
                CreateSkill(RetrievedSkillName, triggerKeywords: "[\"offene dienste\"]")
            });
        SetupRetrievalResult(new RetrievalResult([]));

        var result = await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), "Zeig mir die offene dienste", conversationId: null,
            currentRoute: null, UserId, language: "de");

        result.Functions.Single(f => f.Name == RetrievedSkillName).ToolsetSource
            .ShouldBe(ToolsetSkillSource.Keyword);
    }

    [Test]
    public async Task ExpandedNeighbour_IsMarkedExpansion()
    {
        SetupRetrievalResult(RetrievalHit(RetrievedSkillName));
        _expander.ExpandAsync(
                _agent.Id, Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new List<AgentSkill> { CreateSkill(NeighbourSkillName) });

        var result = await AssembleAsync();

        result.Functions.Single(f => f.Name == NeighbourSkillName).ToolsetSource
            .ShouldBe(ToolsetSkillSource.Expansion);
    }

    [Test]
    public async Task PlanningProfileDraftLoopSkills_AreMarkedHint()
    {
        var userId = Guid.NewGuid();
        const string conversationId = "conv-planning-profile-provenance";
        _planningProfileDraftStore.Set(userId, conversationId, new PlanningProfileDraft());

        var result = await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), "Sicherheitsdienst", conversationId, currentRoute: null,
            userId.ToString(), language: "de");

        result.Functions.Single(f => f.Name == "set_planning_profile_parameters").ToolsetSource
            .ShouldBe(ToolsetSkillSource.Hint);
    }
}
