// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the result an ordinary single-turn replay reports: an unavailable model fails with the
/// orchestrator's message before any provider call, and the first provider answer is mapped field by field onto the
/// result (tool choice, parameters, content, cost, tokens, model identity, toolset, correction flags) and recorded as
/// the first step, for a successful and for a failed provider answer alike.
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
public class TurnReplayServiceCharacterizationTests
{
    private const string ModelId = "model-under-replay";
    private const string UnknownModelId = "model-that-does-not-exist";
    private const string ProviderId = "test-provider";
    private const string Message = "Frau Amstutz hat gekuendigt, bitte aus dem Personalbestand ausbuchen.";
    private const string LookupTool = "search_employees";
    private const string OtherTool = "delete_client";
    private const string SearchedName = "Amstutz";
    private const string FirstContent = "Ich suche die Person.";
    private const string ProviderError = "invalid request payload";

    private static readonly string UserId = Guid.NewGuid().ToString();

    private ILLMProvider _provider = null!;
    private TurnReplayService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var assembler = Substitute.For<ISkillToolsetAssembler>();
        assembler.AssembleAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new SkillToolsetResult
            {
                Functions =
                [
                    new LLMFunction { Name = LookupTool },
                    new LLMFunction { Name = OtherTool }
                ]
            });

        _provider = Substitute.For<ILLMProvider>();

        var repository = Substitute.For<ILLMRepository>();
        repository.GetModelByIdAsync(ModelId).Returns(new LLMModel
        {
            ModelId = ModelId,
            ApiModelId = ModelId,
            ProviderId = ProviderId,
            IsEnabled = true,
            MaxTokens = 4096
        });

        var providerFactory = Substitute.For<ILLMProviderFactory>();
        providerFactory.GetProviderForModelAsync(ModelId).Returns(_provider);

        var skillCache = Substitute.For<ISkillCacheService>();
        skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);
        skillCache.GetAllEnabledSkillsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>());

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

    private static TurnGoldsetItem Item() => new()
    {
        Id = "char-001",
        Message = Message,
        Locale = "de",
        ExpectedTool = OtherTool
    };

    private void GivenProviderAnswers(LLMProviderResponse response)
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(response);
    }

    [Test]
    public async Task AnUnavailableModel_FailsWithTheOrchestratorsMessage_AndCallsNoProvider()
    {
        var result = await _service.ReplayAsync(Item(), UnknownModelId, UserId, new List<string>());

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("The selected model is not available.");
        result.ChosenTool.ShouldBeNull();
        result.Steps.ShouldBeEmpty();
        await _provider.DidNotReceiveWithAnyArgs().ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    [Test]
    public async Task ASuccessfulAnswer_IsMappedOntoTheResult_AndRecordedAsTheFirstStep()
    {
        var parameters = new Dictionary<string, object> { ["searchTerm"] = SearchedName };
        GivenProviderAnswers(new LLMProviderResponse
        {
            Success = true,
            Content = FirstContent,
            FunctionCalls = [new LLMFunctionCall { FunctionName = LookupTool, Parameters = parameters }],
            Usage = { Cost = 0.25m, InputTokens = 120, OutputTokens = 30 }
        });

        var result = await _service.ReplayAsync(Item(), ModelId, UserId, new List<string>());

        result.Success.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.ChosenTool.ShouldBe(LookupTool);
        result.ToolParameters.ShouldBeSameAs(parameters);
        result.Content.ShouldBe(FirstContent);
        result.Cost.ShouldBe(0.25m);
        result.InputTokens.ShouldBe(120);
        result.OutputTokens.ShouldBe(30);
        result.ProviderId.ShouldBe(ProviderId);
        result.ApiModelId.ShouldBe(ModelId);
        result.AvailableToolNames.ShouldBe(new[] { LookupTool, OtherTool });
        result.EngineRecipeWouldTrigger.ShouldBeFalse();
        result.TriggeredRecipeName.ShouldBeNull();
        result.CorrectionApplied.ShouldBeFalse();
        result.CorrectionClarificationOffered.ShouldBeFalse();
        result.UndoOfferedSkill.ShouldBeNull();
        result.FollowUpAttempted.ShouldBeFalse();

        result.Steps.Count.ShouldBe(1);
        var step = result.Steps[0];
        step.Tool.ShouldBe(LookupTool);
        step.Parameters.ShouldBeSameAs(parameters);
        step.Content.ShouldBe(FirstContent);
        step.LatencyMs.ShouldBe(result.LatencyMs);
        step.Success.ShouldBeTrue();
        step.Error.ShouldBeNull();
    }

    [Test]
    public async Task AFailedAnswer_IsReportedWithItsError_AndRecordedAsAFailedFirstStep()
    {
        GivenProviderAnswers(new LLMProviderResponse { Success = false, Error = ProviderError });

        var result = await _service.ReplayAsync(Item(), ModelId, UserId, new List<string>());

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(ProviderError);
        result.ChosenTool.ShouldBeNull();
        result.ToolParameters.ShouldBeEmpty();
        result.ProviderId.ShouldBe(ProviderId);
        result.Steps.Count.ShouldBe(1);
        result.Steps[0].Success.ShouldBeFalse();
        result.Steps[0].Error.ShouldBe(ProviderError);
        result.Steps[0].Tool.ShouldBeNull();
        await _provider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>());
    }
}
