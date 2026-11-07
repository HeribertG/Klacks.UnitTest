// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PluginWriteBlockedOnPlaygroundFilter: on the Playground a write request to a controller outside the Api
/// assembly (a plugin controller) is answered with the PLAYGROUND_FEATURE_BLOCKED 403 and never reaches the action;
/// reads, Api controllers and every request with the mode off pass through. Also pins the global registration.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Presentation.Controllers.Plugins;
using Klacks.Api.Presentation.Extensions;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PluginWriteBlockedOnPlaygroundFilterTests
{
    private IPlaygroundMode _mode = null!;
    private PluginWriteBlockedOnPlaygroundFilter _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _sut = new PluginWriteBlockedOnPlaygroundFilter(_mode);
    }

    private static ActionExecutingContext Executing(string method, Type controllerType)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = controllerType.GetTypeInfo() };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor, new ModelStateDictionary());
        return new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    private static ActionExecutionDelegate Next(ActionExecutingContext executing, Action onRun) => () =>
    {
        onRun();
        return Task.FromResult(new ActionExecutedContext(executing, new List<IFilterMetadata>(), new object()));
    };

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    [TestCase("PATCH")]
    public async Task ModeOn_WriteToPluginController_AnswersFeatureBlockedAndNeverRunsTheAction(string method)
    {
        _mode.IsActive.Returns(true);
        var executing = Executing(method, typeof(PluginStandInController));
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(executing, () => ran = true));

        ran.ShouldBeFalse();
        var result = executing.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        var problem = result.Value.ShouldBeOfType<ProblemDetails>();
        problem.Extensions["errorCode"].ShouldBe(PlaygroundHardeningConstants.FeatureBlockedErrorCode);
        problem.Detail.ShouldBe(PlaygroundHardeningConstants.FeatureBlockedMessage);
    }

    [TestCase("GET")]
    [TestCase("HEAD")]
    [TestCase("OPTIONS")]
    public async Task ModeOn_ReadFromPluginController_RunsTheAction(string method)
    {
        _mode.IsActive.Returns(true);
        var executing = Executing(method, typeof(PluginStandInController));
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(executing, () => ran = true));

        ran.ShouldBeTrue();
        executing.Result.ShouldBeNull();
    }

    [Test]
    public async Task ModeOn_WriteToApiController_RunsTheAction()
    {
        _mode.IsActive.Returns(true);
        var executing = Executing(HttpMethods.Post, typeof(FeaturePluginController));
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(executing, () => ran = true));

        ran.ShouldBeTrue();
        executing.Result.ShouldBeNull();
    }

    [Test]
    public async Task ModeOff_WriteToPluginController_RunsTheAction()
    {
        _mode.IsActive.Returns(false);
        var executing = Executing(HttpMethods.Post, typeof(PluginStandInController));
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(executing, () => ran = true));

        ran.ShouldBeTrue();
        executing.Result.ShouldBeNull();
    }

    [Test]
    public void TheFilterIsRegisteredGlobally()
    {
        var filters = new FilterCollection().AddKlacksGlobalFilters();

        filters.OfType<TypeFilterAttribute>().Select(filter => filter.ImplementationType)
            .ShouldContain(typeof(PluginWriteBlockedOnPlaygroundFilter));
    }

    private sealed class PluginStandInController : ControllerBase
    {
    }
}
