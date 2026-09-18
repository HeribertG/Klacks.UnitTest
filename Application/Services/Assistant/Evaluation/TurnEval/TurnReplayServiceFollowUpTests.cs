// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The follow-up replay asks the model a second time only when its first choice was a plain lookup in
/// front of an expected mutation, feeds back a synthetic result in the production result format, never
/// forces the second choice, and never lets a failing second call damage the first step. The plain
/// replay keeps making exactly one call.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnReplayServiceFollowUpTests
{
    private const string ModelId = "model-under-replay";
    private const string Message = "Frau Amstutz hat gekuendigt, bitte aus dem Personalbestand ausbuchen.";
    private const string ExpectedTool = "delete_client";
    private const string LookupTool = "search_employees";
    private const string ReadOnlyExpectedTool = "list_groups";
    private const string SearchedName = "Amstutz";
    private const string FirstContent = "Ich suche die Person.";
    private const string ProviderError = "provider unavailable";

    private static readonly string UserId = Guid.NewGuid().ToString();

    private ILLMProvider _provider = null!;
    private List<LLMProviderRequest> _requests = null!;
    private TurnReplayService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var assembler = Substitute.For<ISkillToolsetAssembler>();
        assembler.AssembleAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new SkillToolsetResult
            {
                Functions =
                [
                    new LLMFunction { Name = LookupTool },
                    new LLMFunction { Name = ExpectedTool },
                    new LLMFunction { Name = ReadOnlyExpectedTool },
                    new LLMFunction { Name = SkillNames.SearchAndNavigate }
                ]
            });

        _requests = new List<LLMProviderRequest>();
        _provider = Substitute.For<ILLMProvider>();

        var repository = Substitute.For<ILLMRepository>();
        repository.GetModelByIdAsync(ModelId).Returns(new LLMModel
        {
            ModelId = ModelId,
            ApiModelId = ModelId,
            ProviderId = "test-provider",
            IsEnabled = true,
            MaxTokens = 4096
        });

        var providerFactory = Substitute.For<ILLMProviderFactory>();
        providerFactory.GetProviderForModelAsync(ModelId).Returns(_provider);

        var skillCache = Substitute.For<ISkillCacheService>();
        skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);
        skillCache.GetAllEnabledSkillsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            new() { Name = LookupTool, Effect = SkillEffect.Read },
            new() { Name = ExpectedTool, Effect = SkillEffect.Mutate },
            new() { Name = ReadOnlyExpectedTool, Effect = SkillEffect.Read },
            new() { Name = SkillNames.SearchAndNavigate, Effect = SkillEffect.Read }
        });

        var contextBudgetPolicy = Substitute.For<IContextBudgetPolicy>();
        contextBudgetPolicy.Resolve(Arg.Any<ILLMProvider>(), Arg.Any<LLMModel>())
            .Returns(new ContextBudgetProfile(20, 30, 5, 5, 8_000));

        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetNowAsync(Arg.Any<CancellationToken>()).Returns(DateTimeOffset.UtcNow);
        companyClock.GetTimeZoneResolutionAsync(Arg.Any<CancellationToken>())
            .Returns(new CompanyTimeZoneResolution(TimeZoneInfo.Utc, CompanyTimeZoneSource.Utc));

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new TurnReplayService(
            skillCache,
            assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            new LLMProviderOrchestrator(
                Substitute.For<ILogger<LLMProviderOrchestrator>>(), providerFactory, repository),
            contextAssemblyPipeline: null!,
            new LLMSystemPromptBuilder(
                new PromptTranslationProvider(
                    scopeFactory, Substitute.For<ILogger<PromptTranslationProvider>>()),
                companyClock),
            scopeFactory,
            contextBudgetPolicy,
            Substitute.For<ITurnPreparationService>(),
            Substitute.For<ILogger<TurnReplayService>>());
    }

    private static TurnGoldsetItem Item(string expectedTool = ExpectedTool) => new()
    {
        Id = "fu-001",
        Message = Message,
        Locale = "de",
        ExpectedTool = expectedTool
    };

    private static LLMProviderResponse ToolCall(string tool, string content = "") => new()
    {
        Success = true,
        Content = content,
        FunctionCalls =
        [
            new LLMFunctionCall
            {
                FunctionName = tool,
                Parameters = new Dictionary<string, object> { ["searchTerm"] = SearchedName }
            }
        ]
    };

    private void GivenProviderAnswers(params LLMProviderResponse[] responses)
    {
        var queue = new Queue<LLMProviderResponse>(responses);
        _provider.ProcessAsync(Arg.Do<LLMProviderRequest>(request => _requests.Add(request)))
            .Returns(_ => queue.Dequeue());
    }

    [Test]
    public async Task LookupBeforeExpectedMutation_IsFollowedUp_AndBothStepsAreRecorded()
    {
        GivenProviderAnswers(ToolCall(LookupTool, FirstContent), ToolCall(ExpectedTool));

        var result = await _service.ReplayWithLookupFollowUpAsync(Item(), ModelId, UserId, new List<string>());

        result.FollowUpAttempted.ShouldBeTrue();
        result.FollowUpFailed.ShouldBeFalse();
        result.ChosenTool.ShouldBe(LookupTool);
        result.Steps.Select(step => step.Tool).ShouldBe([LookupTool, ExpectedTool]);
        _requests.Count.ShouldBe(TurnEvalDefaults.MaxReplaySteps);
    }

    [Test]
    public async Task SecondRequest_CarriesTheSyntheticResultInsideTheToolResultBlock_AndIsNeverForced()
    {
        GivenProviderAnswers(ToolCall(LookupTool, FirstContent), ToolCall(ExpectedTool));

        await _service.ReplayWithLookupFollowUpAsync(Item(), ModelId, UserId, new List<string>());

        var second = _requests[1];
        second.ToolChoice.ShouldBeNull();
        second.Message.ShouldContain(ToolResultMarkers.BlockHeader);
        second.Message.ShouldContain(SearchedName);
        second.Message.ShouldContain(TurnEvalDefaults.SyntheticLookupEntityId);
        second.ConversationHistory[^2].Role.ShouldBe(LLMMessageRoles.User);
        second.ConversationHistory[^2].Content.ShouldBe(Message);
        second.ConversationHistory[^1].Role.ShouldBe(LLMMessageRoles.Assistant);
        second.ConversationHistory[^1].Content.ShouldBe(FirstContent);
    }

    [Test]
    public async Task EmptyFirstContent_UsesTheProductionPlaceholderAsAssistantTurn()
    {
        GivenProviderAnswers(ToolCall(LookupTool), ToolCall(ExpectedTool));

        await _service.ReplayWithLookupFollowUpAsync(Item(), ModelId, UserId, new List<string>());

        _requests[1].ConversationHistory[^1].Content.ShouldBe(LLMLoopConstants.ExecutingFunctionCallsPlaceholder);
    }

    [Test]
    public async Task PlainReplay_NeverFollowsUp()
    {
        GivenProviderAnswers(ToolCall(LookupTool), ToolCall(ExpectedTool));

        var result = await _service.ReplayAsync(Item(), ModelId, UserId, new List<string>());

        result.FollowUpAttempted.ShouldBeFalse();
        result.Steps.Count.ShouldBe(1);
        _requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task FirstStepHit_IsNotFollowedUp()
    {
        GivenProviderAnswers(ToolCall(ExpectedTool));

        var result = await _service.ReplayWithLookupFollowUpAsync(Item(), ModelId, UserId, new List<string>());

        result.FollowUpAttempted.ShouldBeFalse();
        _requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task ReadOnlyExpectation_IsNotFollowedUp()
    {
        GivenProviderAnswers(ToolCall(LookupTool));

        var result = await _service.ReplayWithLookupFollowUpAsync(
            Item(ReadOnlyExpectedTool), ModelId, UserId, new List<string>());

        result.FollowUpAttempted.ShouldBeFalse();
    }

    [Test]
    public async Task NavigationChoice_IsNotFollowedUp()
    {
        GivenProviderAnswers(ToolCall(SkillNames.SearchAndNavigate));

        var result = await _service.ReplayWithLookupFollowUpAsync(Item(), ModelId, UserId, new List<string>());

        result.FollowUpAttempted.ShouldBeFalse();
    }

    [Test]
    public async Task NoToolCall_IsNotFollowedUp()
    {
        GivenProviderAnswers(new LLMProviderResponse { Success = true, Content = FirstContent });

        var result = await _service.ReplayWithLookupFollowUpAsync(Item(), ModelId, UserId, new List<string>());

        result.FollowUpAttempted.ShouldBeFalse();
        result.Steps.Count.ShouldBe(1);
    }

    [Test]
    public async Task FailingSecondCall_LeavesTheFirstStepIntact()
    {
        GivenProviderAnswers(
            ToolCall(LookupTool), new LLMProviderResponse { Success = false, Error = ProviderError });

        var result = await _service.ReplayWithLookupFollowUpAsync(Item(), ModelId, UserId, new List<string>());

        result.Success.ShouldBeTrue();
        result.ChosenTool.ShouldBe(LookupTool);
        result.FollowUpAttempted.ShouldBeTrue();
        result.FollowUpFailed.ShouldBeTrue();
        result.Steps[^1].Error.ShouldBe(ProviderError);
    }
}
