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
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
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
    private const string UserMessage = "Bitte such mir die passenden Mitarbeitenden zusammen";
    private const string UserId = "user-1";

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IRetrievalQueryBuilder _retrievalQueryBuilder = null!;
    private ISkillRetrievalExpander _expander = null!;
    private IPendingUserNoteRepository _pendingUserNoteRepository = null!;
    private RecipeEngineService _recipeEngine = null!;
    private Agent _agent = null!;

    [SetUp]
    public void Setup()
    {
        _skillCache = Substitute.For<ISkillCacheService>();
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _retrievalQueryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        _retrievalQueryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

        _agent = new Agent { Id = Guid.NewGuid() };
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(AlwaysOnSkillName, alwaysOn: true),
                CreateSkill(RetrievedSkillName),
                CreateSkill(NeighbourSkillName),
                CreateSkill(RestrictedSkillName, requiredPermission: RequiredRight)
            });

        SetupRetrievalResult(new RetrievalResult([]));
    }

    private SkillToolsetAssembler CreateAssembler()
    {
        return new SkillToolsetAssembler(
            _skillCache, _retrieval, _retrievalQueryBuilder, _expander,
            _pendingUserNoteRepository, _recipeEngine,
            Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

    private void SetupRetrievalResult(RetrievalResult result)
    {
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

    private static AgentSkill CreateSkill(string name, bool alwaysOn = false, string? requiredPermission = null)
    {
        return new AgentSkill
        {
            Name = name,
            Description = "A skill.",
            ParametersJson = "[]",
            AlwaysOn = alwaysOn,
            RequiredPermission = requiredPermission
        };
    }

    private Task<SkillToolsetResult> AssembleAsync(List<string>? userRights = null)
    {
        return CreateAssembler().AssembleAsync(
            _agent, userRights ?? new List<string>(), UserMessage,
            conversationId: null, currentRoute: null, UserId, language: null, CancellationToken.None);
    }

    [Test]
    public async Task AssembleAsync_NullAgent_ReturnsEmptyToolsetWithoutDomainContext()
    {
        var result = await CreateAssembler().AssembleAsync(
            null, new List<string>(), UserMessage, null, null, UserId, null, CancellationToken.None);

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

    [Test]
    public async Task AssembleAsync_RetrievalThrows_FallsBackToAlwaysOn_GateStaysTrue()
    {
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

        LLMContext? streamingContext = null;
        var streamingLLMService = Substitute.For<ILLMService>();
        streamingLLMService.ProcessStreamAsync(
                Arg.Do<LLMContext>(c => streamingContext = c), Arg.Any<CancellationToken>())
            .Returns(EmptyStream());
        var orchestrator = new LLMStreamingOrchestrator(
            streamingLLMService, _skillCache, assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            Substitute.For<ILogger<LLMStreamingOrchestrator>>());

        LLMContext? nonStreamingContext = null;
        var nonStreamingLLMService = Substitute.For<ILLMService>();
        nonStreamingLLMService.ProcessAsync(Arg.Do<LLMContext>(c => nonStreamingContext = c))
            .Returns(new LLMResponse());
        var handler = new ProcessLLMMessageCommandHandler(
            nonStreamingLLMService, Substitute.For<IAgentRepository>(), _skillCache, assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>());

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
}
