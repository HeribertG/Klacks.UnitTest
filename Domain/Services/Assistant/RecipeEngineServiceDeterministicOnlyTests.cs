// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The G5 probe calls GuaranteedSkillNamesAsync with allowSemanticFallback: false: it asks whether a message routes by
/// itself through a keyword trigger. That call must never reach the semantic fallback (an embedding and a rerank round
/// per correction that carries a mutation verb) and must not ask the rights gate about a probe, which is not a question
/// about one user's rights.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceDeterministicOnlyTests
{
    private const string Message = "Erstelle einen neuen Auftrag";
    private const string UnmatchedMessage = "Richte bitte alles Notwendige fuer die neue Filiale ein";

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
    private IAgentSkillRepository _skillRepository = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private RecipeEngineService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OrderRecipe });

        _skillRepository = Klacks.UnitTest.TestHelpers.SkillRepositoryStubs.WithoutRequirements();
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult([]));

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        provider.GetService(typeof(IRecipeCandidateRetriever)).Returns(new KnowledgeIndexRecipeCandidateRetriever(_retrieval));

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(),
            Substitute.For<ILogger<RecipeEngineService>>());
    }

    [Test]
    public async Task DeterministicOnlyCall_OnAKeywordTriggerMatch_GuaranteesTheStepSkills_WithoutAnyRetrieval()
    {
        var skills = await _service.GuaranteedSkillNamesAsync(
            userId: null, conversationId: null, Message, language: null, userRights: null,
            allowSemanticFallback: false, CancellationToken.None);

        skills.ShouldContain("create_shift");
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default);
    }

    [Test]
    public async Task DeterministicOnlyCall_OnAMessageNoTriggerMatches_NeverRunsTheSemanticFallback()
    {
        var skills = await _service.GuaranteedSkillNamesAsync(
            userId: null, conversationId: null, UnmatchedMessage, language: null, userRights: null,
            allowSemanticFallback: false, CancellationToken.None);

        skills.ShouldBeEmpty();
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default);
    }

    [Test]
    public async Task FullCall_OnAMessageNoTriggerMatches_RunsTheSemanticFallback()
    {
        await _service.GuaranteedSkillNamesAsync(userId: null, conversationId: null, UnmatchedMessage);

        await _retrieval.Received(1).RetrieveAsync(
            UnmatchedMessage, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(), Arg.Any<IReadOnlyCollection<string>?>());
    }

    [Test]
    public async Task AProbeWithoutRights_NeverAsksTheRightsGate()
    {
        await _service.GuaranteedSkillNamesAsync(
            userId: null, conversationId: null, Message, language: null, userRights: null,
            allowSemanticFallback: false, CancellationToken.None);

        await _skillRepository.DidNotReceiveWithAnyArgs().GetRequiredPermissionsByNameAsync(default!, default);
    }
}
