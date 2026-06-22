// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies the page-explain route guarantee in the chat skill selection: the explain_page_*
/// skill matching PageContext.CurrentRoute is always offered to the LLM, independent of
/// retrieval quality, and never duplicated when retrieval already surfaced it.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.Infrastructure.KnowledgeIndex.Domain;
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

    private ILLMService _llmService = null!;
    private IAgentRepository _agentRepository = null!;
    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IPlanningScopeEnricher _enricher = null!;
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
        _enricher = Substitute.For<IPlanningScopeEnricher>();

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
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));

        _capturedContext = null;
        _llmService.ProcessAsync(Arg.Do<LLMContext>(c => _capturedContext = c))
            .Returns(new LLMResponse());
    }

    private ProcessLLMMessageCommandHandler CreateHandler()
    {
        return new ProcessLLMMessageCommandHandler(
            _llmService, _agentRepository, _skillCache, _retrieval, _enricher, _recipeEngine);
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
        var entry = new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = DashboardSkillName,
            Text = "explain_page_dashboard. Explains a page."
        };
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([new RetrievalCandidate(entry, 0.9)]));

        await CreateHandler().Handle(CreateCommand("/workplace/dashboard"), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.AvailableFunctions.Count(f => f.Name == DashboardSkillName).ShouldBe(1);
    }
}
