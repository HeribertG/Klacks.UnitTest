// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// No client endpoint may prove a user confirmation. execute-function, execute-functions-batch and skills/execute carry
/// requests that the UI action engine posts with the user's own session while a model controls the parameters, so a
/// confirm_pending_action arriving there proves nothing and must be refused like any call without the chat's own evidence.
/// These tests pin that every such entry point builds a context with UserConfirmationEvidence.None and that the real
/// confirm skill and guard then refuse the redemption and keep the token valid.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class ConfirmationEvidenceEntryPointTests
{
    private ISkillExecutor _executor = null!;
    private ILLMSkillBridge _bridge = null!;
    private SkillExecutionContext? _seenByBridge;
    private SkillExecutionContext? _seenByExecutor;

    [SetUp]
    public void Setup()
    {
        _seenByBridge = null;
        _seenByExecutor = null;
        _executor = Substitute.For<ISkillExecutor>();
        _executor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Do<SkillExecutionContext>(c => _seenByExecutor = c), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ok"));
        _bridge = Substitute.For<ILLMSkillBridge>();
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Do<SkillExecutionContext>(c => _seenByBridge = c), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, Message = "ok" });
    }

    private static ExecuteSkillCommand SkillCommand(string skillName, Guid userId) => new(
        new SkillExecuteRequest { SkillName = skillName, Parameters = new Dictionary<string, object>() },
        userId, Guid.NewGuid(), "caller", new List<string>(), null, null);

    private static ExecuteLLMFunctionCommand FunctionCommand(string functionName, Guid userId) => new()
    {
        FunctionName = functionName,
        Parameters = new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = "t" },
        UserId = userId.ToString(),
        UserRights = new List<string>()
    };

    [TestCase("confirm_pending_action")]
    [TestCase(" Confirm_Pending_Action ")]
    [TestCase("navigate_to")]
    [TestCase("delete_shift")]
    public async Task ExecuteFunction_CarriesNoEvidence_ForAnyFunctionName(string functionName)
    {
        var sut = new ExecuteLLMFunctionCommandHandler(
            Substitute.For<ILogger<ExecuteLLMFunctionCommandHandler>>(), _bridge);

        await sut.Handle(FunctionCommand(functionName, Guid.NewGuid()), CancellationToken.None);

        _seenByBridge.ShouldNotBeNull();
        _seenByBridge.UserConfirmation.ShouldBe(UserConfirmationEvidence.None);
    }

    [TestCase("confirm_pending_action")]
    [TestCase("delete_shift")]
    public async Task ExecuteSkill_CarriesNoEvidence_ForAnyCommand(string skillName)
    {
        var sut = new ExecuteSkillCommandHandler(
            _executor, new SkillMapper(), Substitute.For<ILogger<ExecuteSkillCommandHandler>>());

        await sut.Handle(SkillCommand(skillName, Guid.NewGuid()), CancellationToken.None);

        _seenByExecutor.ShouldNotBeNull();
        _seenByExecutor.UserConfirmation.ShouldBe(UserConfirmationEvidence.None);
    }

    [TestCase("confirm_pending_action")]
    [TestCase("delete_shift")]
    public async Task SkillsControllerExecute_BuildsACommandThatCarriesNoEvidence(string skillName)
    {
        var mediator = Substitute.For<Klacks.Api.Infrastructure.Mediator.IMediator>();
        ExecuteSkillCommand? sent = null;
        mediator.Send(Arg.Do<ExecuteSkillCommand>(command => sent = command), Arg.Any<CancellationToken>())
            .Returns(new SkillExecuteResponse { Success = true, ResultType = SkillResultType.Data });
        var controller = new SkillsController(mediator, Substitute.For<ILogger<SkillsController>>())
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            }
        };

        await controller.ExecuteSkill(new SkillExecuteRequest
        {
            SkillName = skillName,
            Parameters = new Dictionary<string, object>()
        });

        sent.ShouldNotBeNull();
        sent.ExternalAgentAccessMode.ShouldBeNull();
        var context = new SkillMapper().ToContext(
            sent.UserId, sent.TenantId, sent.UserName, sent.UserPermissions, sent.AccessToken, sent.ExternalAgentAccessMode);
        context.UserConfirmation.ShouldBe(UserConfirmationEvidence.None);
    }

    [Test]
    public async Task AConfirmPostedToExecuteFunction_ReachesTheRealConfirmSkill_AndIsRefused_TheTokenStaysValid()
    {
        var userId = Guid.NewGuid();
        var store = PendingStoreTestFactory.CreateConfirmationStore();
        var token = store.Create(userId, "delete_shift", new Dictionary<string, object>());
        var skillExecutor = Substitute.For<ISkillExecutor>();
        var confirmSkill = new ConfirmPendingActionSkill(
            store, skillExecutor, new ConfirmationRedemptionGuard(new TurnConfirmationScope()));
        var handler = new ExecuteLLMFunctionCommandHandler(
            Substitute.For<ILogger<ExecuteLLMFunctionCommandHandler>>(), _bridge);

        await handler.Handle(FunctionCommand("confirm_pending_action", userId), CancellationToken.None);
        var result = await confirmSkill.ExecuteAsync(
            _seenByBridge!, new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ConfirmationRedemptionMessages.UserConfirmationMissing);
        await skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        store.PeekLatestForUser(userId, TimeSpan.FromMinutes(1)).ShouldNotBeNull();
    }
}
