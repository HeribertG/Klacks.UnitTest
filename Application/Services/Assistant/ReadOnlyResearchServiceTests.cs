// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the read-only research sub-loop: it resolves the cheapest model, advertises only
/// read-only tools, caps iterations with a final synthesis pass (MaxIterations + 1 model calls), blocks
/// any tool call outside the read-only allow-list before it reaches the bridge, inherits the caller's
/// execution context, returns the model's synthesis, and degrades gracefully when no model is enabled.
/// The LLM provider and skill bridge are mocked; the real read-only filter + risk classifier are used.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;
using Providers = Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ReadOnlyResearchServiceTests
{
    private const string ReadOnlySkill = "check_absence_conflicts";
    private const string MutatingSkill = "create_employee";
    private const string CheapApiModelId = "api-cheap";

    private static readonly Guid CallerUserId = Guid.NewGuid();

    private ICheapestModelResolver _resolver = null!;
    private ISkillRegistry _registry = null!;
    private IReadOnlyToolsetFilter _filter = null!;
    private ILLMSkillBridge _bridge = null!;
    private Providers.ILLMProvider _provider = null!;
    private LLMModel _model = null!;
    private ReadOnlyResearchService _service = null!;

    private static SkillExecutionContext Context() => new()
    {
        UserId = CallerUserId,
        TenantId = Guid.Empty,
        UserName = "caller",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private static SkillDescriptor Descriptor(string name, SkillCategory category) =>
        new(name, $"{name} description", category,
            Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(),
            ImplementationType: null);

    private static Providers.LLMProviderResponse TextResponse(string content) =>
        new() { Success = true, Content = content };

    private static Providers.LLMProviderResponse ToolResponse(string functionName) =>
        new()
        {
            Success = true,
            Content = string.Empty,
            FunctionCalls = new List<Providers.LLMFunctionCall> { new() { FunctionName = functionName } }
        };

    [SetUp]
    public void SetUp()
    {
        _resolver = Substitute.For<ICheapestModelResolver>();
        _registry = Substitute.For<ISkillRegistry>();
        _filter = new ReadOnlyToolsetFilter(new SkillRiskClassifier());
        _bridge = Substitute.For<ILLMSkillBridge>();
        _provider = Substitute.For<Providers.ILLMProvider>();
        _model = new LLMModel
        {
            ModelId = "cheap",
            ApiModelId = CheapApiModelId,
            CostPerInputToken = 0.05m,
            CostPerOutputToken = 0.05m
        };
        _service = new ReadOnlyResearchService(
            _resolver, _registry, _filter, _bridge,
            Substitute.For<ILogger<ReadOnlyResearchService>>());

        _resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)_model, (Providers.ILLMProvider?)_provider));

        _registry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>()).Returns(new List<SkillDescriptor>
        {
            Descriptor(ReadOnlySkill, SkillCategory.Query),
            Descriptor(MutatingSkill, SkillCategory.Crud)
        });

        _bridge.GetSkillsAsLLMFunctions(Arg.Any<IReadOnlyList<string>>()).Returns(new List<LLMFunction>
        {
            new() { Name = ReadOnlySkill },
            new() { Name = MutatingSkill }
        });

        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<Providers.LLMFunctionCall>(),
                Arg.Any<SkillExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, Message = "data" });
    }

    [Test]
    public async Task DirectAnswer_ReturnsSynthesis_AdvertisesOnlyReadOnlyToolsAndCheapModel()
    {
        _provider.ProcessAsync(Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(TextResponse("SYNTH"));

        var result = await _service.ResearchAsync("analyze the month", Context());

        result.Synthesis.ShouldBe("SYNTH");
        result.ModelAvailable.ShouldBeTrue();
        result.ToolCallCount.ShouldBe(0);

        await _provider.Received(1).ProcessAsync(
            Arg.Is<Providers.LLMProviderRequest>(r =>
                r.ModelId == CheapApiModelId &&
                r.AvailableFunctions.Count == 1 &&
                r.AvailableFunctions[0].Name == ReadOnlySkill),
            Arg.Any<CancellationToken>());

        await _bridge.DidNotReceive().ExecuteSkillFromLLMCallAsync(
            Arg.Any<Providers.LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ToolLoop_CapsIterations_WithOneFinalSynthesisPass()
    {
        _provider.ProcessAsync(Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToolResponse(ReadOnlySkill));

        await _service.ResearchAsync("analyze the month", Context());

        await _provider.Received(ReadOnlyResearchConstants.MaxIterations + 1).ProcessAsync(
            Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>());

        await _bridge.Received(ReadOnlyResearchConstants.MaxIterations).ExecuteSkillFromLLMCallAsync(
            Arg.Any<Providers.LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ToolCalls_InheritCallerExecutionContext()
    {
        _provider.ProcessAsync(Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToolResponse(ReadOnlySkill), TextResponse("done"));

        await _service.ResearchAsync("analyze the month", Context());

        await _bridge.Received().ExecuteSkillFromLLMCallAsync(
            Arg.Any<Providers.LLMFunctionCall>(),
            Arg.Is<SkillExecutionContext>(c => c.UserId == CallerUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NonReadOnlyToolCall_IsBlockedBeforeReachingBridge()
    {
        _provider.ProcessAsync(Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToolResponse(MutatingSkill), TextResponse("final"));

        var result = await _service.ResearchAsync("analyze the month", Context());

        result.Synthesis.ShouldBe("final");
        result.ToolsUsed.ShouldBeEmpty();

        await _bridge.DidNotReceive().ExecuteSkillFromLLMCallAsync(
            Arg.Is<Providers.LLMFunctionCall>(c => c.FunctionName == MutatingSkill),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoEnabledModel_ReturnsUnavailable_WithoutCallingProviderOrBridge()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)null, (Providers.ILLMProvider?)null));

        var result = await _service.ResearchAsync("analyze the month", Context());

        result.ModelAvailable.ShouldBeFalse();
        result.Synthesis.ShouldBe(ReadOnlyResearchConstants.NoModelAvailableMessage);

        await _provider.DidNotReceive().ProcessAsync(
            Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>());
        await _bridge.DidNotReceive().ExecuteSkillFromLLMCallAsync(
            Arg.Any<Providers.LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }
}
