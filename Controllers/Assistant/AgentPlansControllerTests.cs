// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for AgentPlansController.CreateAndStartPlan: a drafted plan without steps is answered with
/// 422 and is neither persisted nor started, a plan with steps is persisted once and started, and the
/// existing 400 (blank goal) and 409 (no default model) guards still fire before any drafting.
/// </summary>

namespace Klacks.UnitTest.Controllers.Assistant;

using System.Security.Claims;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

[TestFixture]
public class AgentPlansControllerTests
{
    private const string UserId = "planner-user";
    private const string Goal = "create a customer and an order";
    private const string EmptyStepsJson = "[]";
    private const string OneStepJson = "[{\"Order\":1,\"Skill\":\"create_shift\",\"Params\":{},\"VerifySkill\":null,\"Reversible\":true}]";

    private IPlanChatService _planChatService = null!;
    private IAgentPlanRepository _planRepository = null!;
    private IPlanStepExecutor _executor = null!;
    private IPlanExecutionRegistry _executionRegistry = null!;
    private AgentPlansController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _planChatService = Substitute.For<IPlanChatService>();
        _planRepository = Substitute.For<IAgentPlanRepository>();
        _executor = Substitute.For<IPlanStepExecutor>();
        _executionRegistry = Substitute.For<IPlanExecutionRegistry>();
        _planChatService.ResolveExecutionProviderAsync(Arg.Any<CancellationToken>())
            .Returns(new PlanProviderResolution(true, null));

        _controller = new AgentPlansController(
            _planChatService,
            _planRepository,
            _executor,
            _executionRegistry,
            NullLogger<AgentPlansController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, UserId) }, "TestAuth"))
                }
            }
        };
    }

    private void GivenDraft(string stepsJson)
    {
        var plan = new AgentPlan { Id = Guid.NewGuid(), UserId = UserId, StepsJson = stepsJson, Status = PlanStatus.Drafting };
        _planChatService
            .DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(plan);
    }

    [TestCase(EmptyStepsJson)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[{\"Order\":1}]")]
    public async Task CreateAndStartPlan_DraftHasNoSteps_Returns422_AndDoesNotPersistOrStart(string stepsJson)
    {
        GivenDraft(stepsJson);

        var result = await _controller.CreateAndStartPlan(new CreatePlanRequest { Goal = Goal }, CancellationToken.None);

        var objectResult = result.ShouldBeOfType<UnprocessableEntityObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        objectResult.Value.ShouldBe("The goal could not be broken down into steps from the available skills.");
        await _planRepository.DidNotReceive().AddAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
        _planChatService.DidNotReceive().StartBackgroundExecution(Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    [Test]
    public async Task CreateAndStartPlan_DraftHasSteps_PersistsOnce_StartsExecution_AndReturns202()
    {
        GivenDraft(OneStepJson);

        var result = await _controller.CreateAndStartPlan(new CreatePlanRequest { Goal = Goal }, CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        var plan = accepted.Value.ShouldBeOfType<AgentPlan>();
        await _planRepository.Received(1).AddAsync(plan, Arg.Any<CancellationToken>());
        _planChatService.Received(1).StartBackgroundExecution(plan.Id, Arg.Any<SkillExecutionContext>(), false);
        Received.InOrder(() =>
        {
            _planRepository.AddAsync(plan, Arg.Any<CancellationToken>());
            _planChatService.StartBackgroundExecution(plan.Id, Arg.Any<SkillExecutionContext>(), false);
        });
    }

    [Test]
    public async Task CreateAndStartPlan_BlankGoal_Returns400_WithoutDrafting()
    {
        var result = await _controller.CreateAndStartPlan(new CreatePlanRequest { Goal = "  " }, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await _planChatService.DidNotReceive().DraftPlanAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
    }

    [Test]
    public async Task CreateAndStartPlan_NoDefaultModel_Returns409_WithoutDrafting()
    {
        _planChatService.ResolveExecutionProviderAsync(Arg.Any<CancellationToken>())
            .Returns(new PlanProviderResolution(false, null));

        var result = await _controller.CreateAndStartPlan(new CreatePlanRequest { Goal = Goal }, CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
        await _planChatService.DidNotReceive().DraftPlanAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
    }
}
