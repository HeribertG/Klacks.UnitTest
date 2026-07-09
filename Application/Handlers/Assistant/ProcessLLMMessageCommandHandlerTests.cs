// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies the non-streaming chat path (ChatController → ProcessLLMMessageCommandHandler) against
/// the shared SkillToolsetAssembler: the page-explain route guarantee, the retrieval failure fallback,
/// the co-required expansion and the domain-skill ontology gate all apply on this path exactly as on
/// the streaming path.
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

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class ProcessLLMMessageCommandHandlerTests
{
    private const string DashboardSkillName = "explain_page_dashboard";
    private const string EmployeesSkillName = "explain_page_employees";
    private const string NeighbourSkillName = "list_contracts";

    private ILLMService _llmService = null!;
    private IAgentRepository _agentRepository = null!;
    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IRetrievalQueryBuilder _retrievalQueryBuilder = null!;
    private ISkillRetrievalExpander _expander = null!;
    private IPlanningScopeEnricher _enricher = null!;
    private IPendingUserNoteRepository _pendingUserNoteRepository = null!;
    private RecipeEngineService _recipeEngine = null!;
    private Agent _agent = null!;
    private LLMContext? _capturedContext;

    [SetUp]
    public void Setup()
    {
        _llmService = Substitute.For<ILLMService>();
        _agentRepository = Substitute.For<IAgentRepository>();
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
        _enricher = Substitute.For<IPlanningScopeEnricher>();
        _pendingUserNoteRepository = Substitute.For<IPendingUserNoteRepository>();
        _pendingUserNoteRepository.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        var pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _recipeEngine = new RecipeEngineService(
            scopeFactory, pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

        _agent = new Agent { Id = Guid.NewGuid() };
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(DashboardSkillName),
                CreateSkill(EmployeesSkillName)
            });

        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));

        _capturedContext = null;
        _llmService.ProcessAsync(Arg.Do<LLMContext>(c => _capturedContext = c))
            .Returns(new LLMResponse());
    }

    private ISkillToolsetAssembler CreateAssembler()
    {
        return new SkillToolsetAssembler(
            _skillCache, _retrieval, _retrievalQueryBuilder, _expander,
            _pendingUserNoteRepository, _recipeEngine,
            Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

    private ProcessLLMMessageCommandHandler CreateHandler()
    {
        return new ProcessLLMMessageCommandHandler(
            _llmService, _agentRepository, _skillCache, CreateAssembler(), _enricher,
            Substitute.For<IEntityCandidateGrounder>());
    }

    private static AgentSkill CreateSkill(string name)
    {
        return new AgentSkill
        {
            Name = name,
            Description = "Explains a page.",
            ParametersJson = "[]",
            AlwaysOn = false
        };
    }

    private static ProcessLLMMessageCommand CreateCommand(string? currentRoute)
    {
        return new ProcessLLMMessageCommand
        {
            Message = "Bitte erkläre mir Abdeckung & Bestätigung",
            UserId = "user-1",
            PageContext = currentRoute == null
                ? null
                : new AssistantPageContext { CurrentRoute = currentRoute }
        };
    }

    private RetrievalResult RetrievalHit(string skillName)
    {
        var entry = new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = skillName,
            Text = $"{skillName}. Explains a page."
        };
        return new RetrievalResult([new RetrievalCandidate(entry, 0.9)]);
    }

    [Test]
    public async Task Handle_OnDashboardRoute_GuaranteesDashboardExplainSkill()
    {
        await CreateHandler().Handle(CreateCommand("/workplace/dashboard"), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.AvailableFunctions.ShouldContain(f => f.Name == DashboardSkillName);
        _capturedContext.AvailableFunctions.ShouldNotContain(f => f.Name == EmployeesSkillName);
    }

    [Test]
    public async Task Handle_RouteWithEntityId_GuaranteesMatchingExplainSkill()
    {
        await CreateHandler().Handle(
            CreateCommand("/workplace/edit-address/0c4f2e1a-aaaa-bbbb-cccc-000000000001"),
            CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.AvailableFunctions.ShouldContain(f => f.Name == EmployeesSkillName);
    }

    [Test]
    public async Task Handle_WithoutPageContext_OffersNoExplainSkill()
    {
        await CreateHandler().Handle(CreateCommand(null), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.AvailableFunctions.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_SkillAlreadyRetrieved_IsNotDuplicated()
    {
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RetrievalHit(DashboardSkillName));

        await CreateHandler().Handle(CreateCommand("/workplace/dashboard"), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.AvailableFunctions.Count(f => f.Name == DashboardSkillName).ShouldBe(1);
    }

    [Test]
    public async Task Handle_RetrievalThrows_FallsBackToAlwaysOnSkills_DoesNotCrash()
    {
        // Regression guard: this handler is the one caller of IKnowledgeRetrievalService.RetrieveAsync
        // (ChatController's non-streaming path) that used to have no try/catch around the call, unlike
        // RecipeEngineService/LLMStreamingOrchestrator. That gap was inert while NullEmbeddingProvider
        // (dead-ONNX platforms) only ever returned zero vectors and never threw. It becomes reachable
        // now that a GeminiEmbeddingProvider fallback exists that fails loud (e.g. missing/invalid key).
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<RetrievalResult>(_ => throw new InvalidOperationException("embedding API key missing"));

        await CreateHandler().Handle(CreateCommand("/workplace/dashboard"), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.AvailableFunctions.ShouldContain(f => f.Name == DashboardSkillName);
        _capturedContext.AvailableFunctions.ShouldNotContain(f => f.Name == EmployeesSkillName);
        _capturedContext.HasDomainSkillContext.ShouldBe(true);
    }

    [Test]
    public async Task Handle_ConversationalTurn_SetsHasDomainSkillContextFalse()
    {
        // Ontology gate on the non-streaming path: empty retrieval and no guarantee firing means a
        // purely conversational turn, so the world-model ontology block may be omitted.
        await CreateHandler().Handle(CreateCommand(null), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.HasDomainSkillContext.ShouldBe(false);
    }

    [Test]
    public async Task Handle_RetrievalHit_SetsHasDomainSkillContextTrue()
    {
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RetrievalHit(DashboardSkillName));

        await CreateHandler().Handle(CreateCommand(null), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.HasDomainSkillContext.ShouldBe(true);
    }

    [Test]
    public async Task Handle_ExpanderNeighbour_IsIncludedInToolset()
    {
        // Co-required expansion on the non-streaming path: a high-confidence neighbour returned by
        // ISkillRetrievalExpander must reach the LLM toolset exactly as on the streaming path.
        var neighbour = CreateSkill(NeighbourSkillName);
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(DashboardSkillName),
                CreateSkill(EmployeesSkillName),
                neighbour
            });
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RetrievalHit(DashboardSkillName));
        _expander.ExpandAsync(
                _agent.Id, Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill> { neighbour });

        await CreateHandler().Handle(CreateCommand(null), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.AvailableFunctions.ShouldContain(f => f.Name == DashboardSkillName);
        _capturedContext.AvailableFunctions.ShouldContain(f => f.Name == NeighbourSkillName);
    }
}
