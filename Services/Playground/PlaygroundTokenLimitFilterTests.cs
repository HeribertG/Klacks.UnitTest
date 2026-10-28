// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundTokenLimitFilter: with the mode off it is a pure pass-through that never asks the budget;
/// with the mode on an exhausted visitor is answered with HTTP 429 and errorCode PLAYGROUND_TOKEN_LIMIT before the
/// action runs, a call the budget refused during a non-streaming action is turned into the same answer, and a
/// response that already started is left alone. Also pins the attribute on the four token-spending endpoints.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Playground;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundTokenLimitFilterTests
{
    private IPlaygroundMode _mode = null!;
    private IPlaygroundTokenBudget _budget = null!;
    private PlaygroundTokenLimitFilter _sut = null!;
    private DefaultHttpContext _http = null!;
    private ActionContext _actionContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _budget = Substitute.For<IPlaygroundTokenBudget>();
        _sut = new PlaygroundTokenLimitFilter(_mode, _budget);
        _http = new DefaultHttpContext();
        _actionContext = new ActionContext(_http, new RouteData(), new ActionDescriptor());
        PlaygroundVisitorContext.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        PlaygroundVisitorContext.Clear();
    }

    private ActionExecutingContext Executing() =>
        new(_actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());

    private ActionExecutionDelegate Next(ActionExecutingContext executing, Action? inAction = null, IActionResult? result = null)
    {
        return () =>
        {
            inAction?.Invoke();
            var executed = new ActionExecutedContext(_actionContext, new List<IFilterMetadata>(), new object())
            {
                Result = result ?? new OkResult(),
            };
            return Task.FromResult(executed);
        };
    }

    private static void AssertLimitResult(IActionResult? result)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        var problem = objectResult.Value.ShouldBeOfType<ProblemDetails>();
        problem.Status.ShouldBe(StatusCodes.Status429TooManyRequests);
        problem.Extensions["errorCode"].ShouldBe(PlaygroundTokenLimitConstants.ErrorCode);
    }

    [Test]
    public async Task ModeOff_RunsTheActionAndNeverAsksTheBudget()
    {
        _mode.IsActive.Returns(false);
        var executing = Executing();
        var actionRan = false;

        await _sut.OnActionExecutionAsync(executing, Next(executing, () => actionRan = true));

        actionRan.ShouldBeTrue();
        executing.Result.ShouldBeNull();
        _budget.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ModeOn_BudgetOpen_RunsTheAction()
    {
        _mode.IsActive.Returns(true);
        _budget.IsExhausted(Arg.Any<PlaygroundVisitor?>()).Returns(false);
        var executing = Executing();
        var actionRan = false;

        await _sut.OnActionExecutionAsync(executing, Next(executing, () => actionRan = true));

        actionRan.ShouldBeTrue();
        executing.Result.ShouldBeNull();
    }

    [Test]
    public async Task ModeOn_BudgetExhausted_Answers429BeforeTheActionRuns()
    {
        _mode.IsActive.Returns(true);
        var visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(visitor);
        _budget.IsExhausted(visitor).Returns(true);
        var executing = Executing();
        var actionRan = false;

        await _sut.OnActionExecutionAsync(executing, Next(executing, () => actionRan = true));

        actionRan.ShouldBeFalse();
        AssertLimitResult(executing.Result);
    }

    [Test]
    public async Task ModeOn_CallRefusedDuringTheAction_ReplacesTheResultWith429()
    {
        _mode.IsActive.Returns(true);
        var visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(visitor);
        _budget.IsExhausted(visitor).Returns(false);
        var executing = Executing();
        ActionExecutedContext? executed = null;
        ActionExecutionDelegate next = async () =>
        {
            visitor.MarkTokenLimitHit();
            executed = await Next(executing)();
            return executed;
        };

        await _sut.OnActionExecutionAsync(executing, next);

        AssertLimitResult(executed!.Result);
    }

    [Test]
    public async Task ModeOn_RefusalAfterTheResponseStarted_LeavesTheResultAlone()
    {
        _mode.IsActive.Returns(true);
        var visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(visitor);
        _budget.IsExhausted(visitor).Returns(false);
        var started = Substitute.For<IHttpResponseFeature>();
        started.HasStarted.Returns(true);
        _http.Features.Set(started);
        var executing = Executing();
        ActionExecutedContext? executed = null;
        ActionExecutionDelegate next = async () =>
        {
            visitor.MarkTokenLimitHit();
            executed = await Next(executing)();
            return executed;
        };

        await _sut.OnActionExecutionAsync(executing, next);

        executed!.Result.ShouldBeOfType<OkResult>();
    }

    [TestCase(nameof(ChatController.ProcessMessage))]
    [TestCase(nameof(ChatController.ProcessMessageStream))]
    [TestCase(nameof(ChatController.ExecuteFunction))]
    [TestCase(nameof(ChatController.ExecuteFunctionsBatch))]
    public void TokenSpendingEndpoints_CarryThePlaygroundFilter(string actionName)
    {
        var method = typeof(ChatController).GetMethod(actionName)!;

        method.GetCustomAttribute<PlaygroundTokenLimitAttribute>().ShouldNotBeNull();
    }

    [Test]
    public void ChatController_StampsTheCodeOnErrorEvents_OnlyWhenTheBudgetRefusedACallOfThisRequest()
    {
        var stamp = typeof(ChatController).GetMethod("StampPlaygroundTokenLimit", BindingFlags.NonPublic | BindingFlags.Static)!;
        var visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(visitor);

        var beforeRefusal = SseChunk.Error("generic failure");
        stamp.Invoke(null, new object[] { beforeRefusal });
        beforeRefusal.ErrorCode.ShouldBeNull();
        beforeRefusal.ErrorMessage.ShouldBe("generic failure");

        visitor.MarkTokenLimitHit();

        var error = SseChunk.Error("generic failure");
        stamp.Invoke(null, new object[] { error });
        error.ErrorCode.ShouldBe(PlaygroundTokenLimitConstants.ErrorCode);
        error.ErrorMessage.ShouldBe(PlaygroundTokenLimitConstants.LimitReachedMessage);

        var content = SseChunk.Content("text");
        stamp.Invoke(null, new object[] { content });
        content.ErrorCode.ShouldBeNull();
    }

    [Test]
    public void Attribute_ResolvesTheFilterFromDi()
    {
        var attribute = new PlaygroundTokenLimitAttribute();

        attribute.ServiceType.ShouldBe(typeof(PlaygroundTokenLimitFilter));
    }
}
