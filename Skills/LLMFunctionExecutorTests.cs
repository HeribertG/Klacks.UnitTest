// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMFunctionExecutor — verifies the deterministic server-side page knowledge
/// injection: navigate_to onto an explainable route executes the explain_page_* skill
/// (level=elements) and appends its content to the navigation result, while non-explainable
/// routes, other skills and injection failures leave the navigation result untouched.
/// Also verifies that UiAction skills route through the skill bridge (no chokepoint bypass) and
/// that UI action steps and replayed original parameters are lifted from the bridge result onto
/// the function call for the frontend.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class LLMFunctionExecutorTests
{
    private ILLMSkillBridge _skillBridge = null!;
    private IAgentSkillRepository _agentSkillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private LLMFunctionExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _agentSkillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        _executor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            _agentSkillRepository,
            _agentRepository,
            PendingStoreTestFactory.CreateConfirmationStore(),
            _skillBridge);
    }

    private static LLMContext Ctx() => new()
    {
        Message = "Wie erstelle ich eine Bestellung?",
        UserId = Guid.NewGuid().ToString(),
        UserRights = new List<string>()
    };

    private static SkillBridgeResult NavigationResult(string route, string? target = null) => new()
    {
        Success = true,
        Message = "Navigate to shift",
        Data = new { Page = "shift", Route = route, Target = target },
        ResultType = nameof(Klacks.Api.Domain.Enums.SkillResultType.Navigation)
    };

    private static SkillBridgeResult KnowledgeResult(string knowledge) => new()
    {
        Success = true,
        Message = "Curated Klacks knowledge.",
        Data = new { Knowledge = knowledge, Level = KnowledgeHappenLevels.Elements }
    };

    private void SetupBridgeFor(string functionName, SkillBridgeResult result)
    {
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Is<LLMFunctionCall>(c => c.FunctionName == functionName),
                Arg.Any<SkillExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(result);
    }

    [Test]
    public async Task NavigateTo_ExplainableRoute_AppendsPageKnowledgeDeterministically()
    {
        SetupBridgeFor(SkillNames.NavigateTo, NavigationResult("/workplace/shift"));
        SetupBridgeFor("explain_page_shifts", KnowledgeResult("SHIFT PAGE KNOWLEDGE"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillNames.NavigateTo, Parameters = new() { ["page"] = "shift" } }
        };

        var result = await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(result, Does.Contain("Navigate to shift"));
        Assert.That(result, Does.Contain("SHIFT PAGE KNOWLEDGE"));
        Assert.That(result, Does.Contain("explain_page_shifts"));
        Assert.That(_executor.NavigationRoute, Is.EqualTo("/workplace/shift"));
        await _skillBridge.Received(1).ExecuteSkillFromLLMCallAsync(
            Arg.Is<LLMFunctionCall>(c =>
                c.FunctionName == "explain_page_shifts" &&
                c.Parameters[KnowledgeHappenLevels.ParameterName].ToString() == KnowledgeHappenLevels.Elements),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NavigateTo_ExplainableRouteWithEntityId_ResolvesExplainSkillFromBaseRoute()
    {
        SetupBridgeFor(SkillNames.NavigateTo, NavigationResult($"/workplace/edit-shift/{Guid.NewGuid()}"));
        SetupBridgeFor("explain_page_shifts", KnowledgeResult("SHIFT PAGE KNOWLEDGE"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillNames.NavigateTo, Parameters = new() { ["page"] = "edit-shift" } }
        };

        var result = await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(result, Does.Contain("SHIFT PAGE KNOWLEDGE"));
    }

    [Test]
    public async Task NavigateTo_RouteWithoutExplainSkill_DoesNotInject()
    {
        SetupBridgeFor(SkillNames.NavigateTo, NavigationResult("/workplace/floor-plan"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillNames.NavigateTo, Parameters = new() { ["page"] = "floor-plan" } }
        };

        var result = await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(result, Does.Contain("Navigate to shift"));
        Assert.That(result, Does.Not.Contain("Server-included page knowledge"));
        await _skillBridge.Received(1).ExecuteSkillFromLLMCallAsync(
            Arg.Any<LLMFunctionCall>(),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OtherSkill_WithNavigationResult_DoesNotInject()
    {
        SetupBridgeFor("search_and_navigate", NavigationResult("/workplace/shift"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = "search_and_navigate", Parameters = new() { ["query"] = "x" } }
        };

        var result = await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(result, Does.Not.Contain("Server-included page knowledge"));
        Assert.That(_executor.NavigationRoute, Is.EqualTo("/workplace/shift"));
        await _skillBridge.Received(1).ExecuteSkillFromLLMCallAsync(
            Arg.Any<LLMFunctionCall>(),
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NavigateTo_ExplainSkillFails_StillReturnsNavigationResult()
    {
        SetupBridgeFor(SkillNames.NavigateTo, NavigationResult("/workplace/shift"));
        SetupBridgeFor("explain_page_shifts", new SkillBridgeResult
        {
            Success = false,
            Message = "Knowledge entry is not available."
        });
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillNames.NavigateTo, Parameters = new() { ["page"] = "shift" } }
        };

        var result = await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(result, Does.Contain("Navigate to shift"));
        Assert.That(result, Does.Not.Contain("Server-included page knowledge"));
        Assert.That(_executor.NavigationRoute, Is.EqualTo("/workplace/shift"));
    }

    [Test]
    public async Task NavigateTo_ExplainSkillThrows_StillReturnsNavigationResult()
    {
        SetupBridgeFor(SkillNames.NavigateTo, NavigationResult("/workplace/shift"));
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Is<LLMFunctionCall>(c => c.FunctionName == "explain_page_shifts"),
                Arg.Any<SkillExecutionContext>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillNames.NavigateTo, Parameters = new() { ["page"] = "shift" } }
        };

        var result = await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(result, Does.Contain("Navigate to shift"));
        Assert.That(result, Does.Not.Contain("Server-included page knowledge"));
    }

    [Test]
    public async Task NavigateTo_WithTargetInResult_SetsNavigationTarget()
    {
        SetupBridgeFor(SkillNames.NavigateTo, NavigationResult("/workplace/settings", "macros"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillNames.NavigateTo, Parameters = new() { ["page"] = "settings", ["target"] = "macros" } }
        };

        await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(_executor.NavigationRoute, Is.EqualTo("/workplace/settings"));
        Assert.That(_executor.NavigationTarget, Is.EqualTo("macros"));
    }

    [Test]
    public async Task NavigateTo_WithoutTargetInResult_NavigationTargetIsNull()
    {
        SetupBridgeFor(SkillNames.NavigateTo, NavigationResult("/workplace/shift"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillNames.NavigateTo, Parameters = new() { ["page"] = "shift" } }
        };

        await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(_executor.NavigationRoute, Is.EqualTo("/workplace/shift"));
        Assert.That(_executor.NavigationTarget, Is.Null);
    }

    private void SetupUiActionSkill(string skillName, string handlerConfig)
    {
        var agent = new Agent { Id = Guid.NewGuid(), Name = "default", IsDefault = true };
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(agent);
        _agentSkillRepository.GetEnabledAsync(agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                new()
                {
                    AgentId = agent.Id,
                    Name = skillName,
                    ExecutionType = LlmExecutionTypes.UiAction,
                    HandlerConfig = handlerConfig
                }
            });
    }

    [Test]
    public async Task UiActionSkill_RoutesThroughSkillBridge_AndLiftsStepsFromResult()
    {
        const string bridgeSteps = "{\"steps\":[{\"action\":\"navigate\",\"route\":\"/workplace/settings\"}]}";
        SetupUiActionSkill("update_branch", "{\"steps\":[{\"action\":\"stale-repo-config\"}]}");
        SetupBridgeFor("update_branch", new SkillBridgeResult
        {
            Success = true,
            Message = "Function 'update_branch' will be executed as UI action in the user's browser.",
            UiActionSteps = bridgeSteps
        });
        var call = new LLMFunctionCall { FunctionName = "update_branch", Parameters = new() { ["name"] = "North" } };

        await _executor.ProcessFunctionCallsAsync(Ctx(), new List<LLMFunctionCall> { call });

        Assert.That(call.UiActionSteps, Is.EqualTo(bridgeSteps));
        Assert.That(call.Success, Is.True);
        await _skillBridge.Received(1).ExecuteSkillFromLLMCallAsync(
            Arg.Is<LLMFunctionCall>(c => c.FunctionName == "update_branch"),
            Arg.Is<SkillExecutionContext>(c => c.SupportsUiActions),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UiActionSkill_HeldByGate_HasNoStepsAndRequiresConfirmation()
    {
        SetupUiActionSkill("update_branch", "{\"steps\":[{\"action\":\"navigate\"}]}");
        SetupBridgeFor("update_branch", new SkillBridgeResult
        {
            Success = false,
            Message = "User confirmation required",
            ResultType = nameof(Klacks.Api.Domain.Enums.SkillResultType.Confirmation)
        });
        var call = new LLMFunctionCall { FunctionName = "update_branch", Parameters = new() { ["name"] = "North" } };

        var result = await _executor.ProcessFunctionCallsAsync(Ctx(), new List<LLMFunctionCall> { call });

        Assert.That(call.UiActionSteps, Is.Null);
        Assert.That(call.RequiresConfirmation, Is.True);
        Assert.That(result, Does.Contain("Confirmation required"));
    }

    [Test]
    public async Task ConfirmedUiActionReplay_LiftsStepsAndOriginalParametersOntoTheConfirmCall()
    {
        const string bridgeSteps = "{\"steps\":[{\"action\":\"setValue\",\"selector\":\"branches-modal-name\"}]}";
        var originalParameters = new Dictionary<string, object> { ["branchId"] = "7", ["name"] = "North" };
        SetupBridgeFor("confirm_pending_action", new SkillBridgeResult
        {
            Success = true,
            Message = "replayed",
            UiActionSteps = bridgeSteps,
            UiActionParameters = originalParameters
        });
        var call = new LLMFunctionCall
        {
            FunctionName = "confirm_pending_action",
            Parameters = new() { ["confirmation_token"] = "token-1" }
        };

        await _executor.ProcessFunctionCallsAsync(Ctx(), new List<LLMFunctionCall> { call });

        Assert.That(call.UiActionSteps, Is.EqualTo(bridgeSteps));
        Assert.That(call.Parameters, Is.SameAs(originalParameters));
    }

    [Test]
    public async Task OtherSkill_WithTargetInNavigationResult_SetsNavigationTarget()
    {
        SetupBridgeFor("search_and_navigate", NavigationResult("/workplace/edit-address/123", "address-contracts"));
        var calls = new List<LLMFunctionCall>
        {
            new() { FunctionName = "search_and_navigate", Parameters = new() { ["query"] = "Müller" } }
        };

        await _executor.ProcessFunctionCallsAsync(Ctx(), calls);

        Assert.That(_executor.NavigationRoute, Is.EqualTo("/workplace/edit-address/123"));
        Assert.That(_executor.NavigationTarget, Is.EqualTo("address-contracts"));
    }
}
