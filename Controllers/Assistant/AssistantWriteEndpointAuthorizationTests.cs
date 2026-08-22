// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the administrator gate on the assistant write endpoints. Global rules and soul sections are
/// part of the system prompt of every session, the transcription dictionary is global, and the model
/// sync and speech check trigger paid external provider calls — each of these was reachable by any
/// authenticated user. Every gate must also keep the explicit JWT scheme, because AddIdentity
/// overrides the runtime default to cookie authentication and a bare role gate would 401 every JWT
/// caller. The creation of a shared memory is gated inside the action instead of by an attribute,
/// because only the payload says whether the new memory is personal or company-wide.
/// </summary>

using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class AssistantWriteEndpointAuthorizationTests
{
    public static IEnumerable<TestCaseData> AdminOnlyWriteEndpoints()
    {
        yield return new TestCaseData(typeof(GlobalRulesController), nameof(GlobalRulesController.Upsert));
        yield return new TestCaseData(typeof(GlobalRulesController), nameof(GlobalRulesController.Deactivate));
        yield return new TestCaseData(typeof(AgentSoulController), nameof(AgentSoulController.UpsertSoulSection));
        yield return new TestCaseData(typeof(AgentSoulController), nameof(AgentSoulController.DeactivateSoulSection));
        yield return new TestCaseData(typeof(TranscriptionDictionaryController), nameof(TranscriptionDictionaryController.Create));
        yield return new TestCaseData(typeof(TranscriptionDictionaryController), nameof(TranscriptionDictionaryController.Update));
        yield return new TestCaseData(typeof(TranscriptionDictionaryController), nameof(TranscriptionDictionaryController.Delete));
        yield return new TestCaseData(typeof(ModelSyncController), nameof(ModelSyncController.TriggerSync));
        yield return new TestCaseData(typeof(ModelSyncController), nameof(ModelSyncController.MarkAllRead));
        yield return new TestCaseData(typeof(ModelsController), nameof(ModelsController.CheckSpeechModels));
    }

    [TestCaseSource(nameof(AdminOnlyWriteEndpoints))]
    public void WriteEndpoint_RequiresTheAdminRole(Type controllerType, string methodName)
    {
        var attribute = GetAuthorizeAttribute(controllerType, methodName);

        attribute.ShouldNotBeNull(
            $"{controllerType.Name}.{methodName} changes state for every user and must carry its own [Authorize].");
        attribute!.Roles.ShouldBe(
            Roles.Admin,
            $"{controllerType.Name}.{methodName} must be restricted to administrators.");
    }

    [TestCaseSource(nameof(AdminOnlyWriteEndpoints))]
    public void WriteEndpoint_PinsTheJwtScheme(Type controllerType, string methodName)
    {
        var attribute = GetAuthorizeAttribute(controllerType, methodName);

        attribute!.AuthenticationSchemes.ShouldBe(
            JwtBearerDefaults.AuthenticationScheme,
            $"{controllerType.Name}.{methodName} must pin the JWT scheme — AddIdentity overrides the " +
            "runtime default to cookie authentication, so a bare role gate 401s every JWT caller.");
    }

    [Test]
    public async Task CreateMemory_NonAdminCreatingASharedMemory_IsForbidden()
    {
        var mediator = Substitute.For<IMediator>();
        var controller = BuildMemoriesController(mediator, isAdmin: false);

        var result = await controller.CreateMemory(
            Guid.NewGuid(),
            new CreateMemoryRequest("k", "c", MemoryCategories.Fact, null, null, null),
            CancellationToken.None);

        var forbid = result.ShouldBeOfType<ForbidResult>();
        forbid.AuthenticationSchemes.ShouldBe(new[] { JwtBearerDefaults.AuthenticationScheme },
            "a bare Forbid() resolves to the cookie scheme AddIdentity installed as the default and " +
            "redirects instead of answering 403.");
        await mediator.DidNotReceive().Send(Arg.Any<CreateAgentMemoryCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateMemory_NonAdminCreatingAPersonalMemory_IsAllowed()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateAgentMemoryCommand>(), Arg.Any<CancellationToken>()).Returns(new object());
        var controller = BuildMemoriesController(mediator, isAdmin: false);

        var result = await controller.CreateMemory(
            Guid.NewGuid(),
            new CreateMemoryRequest("k", "c", MemoryCategories.Preference, null, null, null),
            CancellationToken.None);

        result.ShouldNotBeOfType<ForbidResult>();
        await mediator.Received(1).Send(Arg.Any<CreateAgentMemoryCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateMemory_AdminCreatingASharedMemory_IsAllowed()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateAgentMemoryCommand>(), Arg.Any<CancellationToken>()).Returns(new object());
        var controller = BuildMemoriesController(mediator, isAdmin: true);

        var result = await controller.CreateMemory(
            Guid.NewGuid(),
            new CreateMemoryRequest("k", "c", MemoryCategories.Fact, null, null, null),
            CancellationToken.None);

        result.ShouldNotBeOfType<ForbidResult>();
        await mediator.Received(1).Send(Arg.Any<CreateAgentMemoryCommand>(), Arg.Any<CancellationToken>());
    }

    private static AgentMemoriesController BuildMemoriesController(IMediator mediator, bool isAdmin)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, Roles.Admin));
        }

        return new AgentMemoriesController(mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
                }
            }
        };
    }

    private static AuthorizeAttribute? GetAuthorizeAttribute(Type controllerType, string methodName) =>
        controllerType
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
}
