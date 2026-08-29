// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Presentation.Filters;

[TestFixture]
public class AdminSetupGateFilterTests
{
    private IAdminSetupGateService _gateService = null!;
    private AdminSetupGateFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _gateService = Substitute.For<IAdminSetupGateService>();
        _filter = new AdminSetupGateFilter();
    }

    [Test]
    public async Task OnActionExecutionAsync_SeedAdminAndGateActive_ShortCircuitsWith403()
    {
        _gateService.IsGateActiveAsync().Returns(true);
        var context = CreateContext(SystemAccounts.SeedAdminUserId, Array.Empty<object>());

        await _filter.OnActionExecutionAsync(context, () => Next(context));

        var result = context.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        var problem = result.Value.ShouldBeOfType<ProblemDetails>();
        problem.Extensions["errorCode"].ShouldBe(Klacks.Api.Application.Constants.DeploymentConstants.SetupRequiredErrorCode);
    }

    [Test]
    public async Task OnActionExecutionAsync_SeedAdminAndGateInactive_CallsNext()
    {
        _gateService.IsGateActiveAsync().Returns(false);
        var context = CreateContext(SystemAccounts.SeedAdminUserId, Array.Empty<object>());

        await _filter.OnActionExecutionAsync(context, () => Next(context));

        context.Result.ShouldBeNull();
    }

    [Test]
    public async Task OnActionExecutionAsync_DifferentUserAndGateActive_CallsNext()
    {
        _gateService.IsGateActiveAsync().Returns(true);
        var context = CreateContext("some-other-user-id", Array.Empty<object>());

        await _filter.OnActionExecutionAsync(context, () => Next(context));

        context.Result.ShouldBeNull();
    }

    [Test]
    public async Task OnActionExecutionAsync_ExemptEndpoint_CallsNextEvenForSeedAdmin()
    {
        _gateService.IsGateActiveAsync().Returns(true);
        var context = CreateContext(SystemAccounts.SeedAdminUserId, new object[] { new ExemptFromAdminSetupGateAttribute() });

        await _filter.OnActionExecutionAsync(context, () => Next(context));

        context.Result.ShouldBeNull();
        await _gateService.DidNotReceive().IsGateActiveAsync();
    }

    private ActionExecutingContext CreateContext(string userId, IReadOnlyList<object> endpointMetadata)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAdminSetupGateService>(_gateService);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth"));

        var actionDescriptor = new ControllerActionDescriptor { EndpointMetadata = endpointMetadata.ToList() };
        var actionContext = new ActionContext(httpContext, new RouteData(), actionDescriptor, new ModelStateDictionary());

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private static Task<ActionExecutedContext> Next(ActionExecutingContext context)
    {
        return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller));
    }
}
