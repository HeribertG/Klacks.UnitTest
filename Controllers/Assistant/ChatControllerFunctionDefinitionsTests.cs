// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the two skill-catalogue endpoints of ChatController. GET chat/functions always filtered the
/// catalogue by the caller's rights; GET chat/function-definitions returned every enabled skill with its full
/// parameter schema and required permission to any signed-in user, which handed a Planer the blueprint of the
/// Admin-only skills. Both endpoints now hide a skill the caller may not run, and the Admin sees all of them.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class ChatControllerFunctionDefinitionsTests
{
    private const string AdminOnlySkill = "create_test_environment";
    private const string SupervisorSkill = "update_client";
    private const string FloorSkill = "add_client_note";
    private const string UngatedSkill = "get_current_date";
    private const string AdminRoleGatedSkill = "list_period_issues";

    private IAgentRepository _agentRepository = null!;
    private IAgentSkillRepository _agentSkillRepository = null!;
    private readonly Guid _agentId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentSkillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = _agentId });
        _agentSkillRepository.GetEnabledAsync(_agentId, Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            Skill(AdminOnlySkill, Permissions.CanEditSettings),
            Skill(SupervisorSkill, Permissions.CanEditClients),
            Skill(FloorSkill, Permissions.CanEditClientNotes),
            Skill(UngatedSkill, null),
            Skill(AdminRoleGatedSkill, Roles.Admin)
        });
    }

    private static AgentSkill Skill(string name, string? requiredPermission) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = $"{name} description",
        RequiredPermission = requiredPermission
    };

    private ChatController ControllerFor(params string[] roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role))
            .Append(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        return new ChatController(
            Substitute.For<ILogger<ChatController>>(),
            Substitute.For<IMediator>(),
            _agentSkillRepository,
            _agentRepository,
            Substitute.For<ILLMStreamingOrchestrator>(),
            Substitute.For<ISkillCacheService>(),
            Substitute.For<IUtteranceNormalizer>(),
            Substitute.For<INavigationTargetMatcher>(),
            Substitute.For<INavigationTargetCacheService>(),
            Substitute.For<INavigationFeedbackLogger>(),
            Substitute.For<INavigationMissDetector>(),
            Substitute.For<ILLMRepository>(),
            Substitute.For<IUserActivityTracker>(),
            Substitute.For<INavigationEntityRouteGuard>(),
            Substitute.For<IActiveTurnRegistry>(),
            Substitute.For<IInterruptedTurnFinalizer>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static List<string> NamesOf(ActionResult<object> result)
    {
        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        return JsonSerializer.SerializeToElement(ok.Value)
            .EnumerateArray()
            .Select(item => item.GetProperty("Name").GetString()!)
            .ToList();
    }

    [Test]
    public async Task FunctionDefinitions_ForAPlaner_HidesEverySkillOutsideTheFloor()
    {
        var names = NamesOf(await ControllerFor().GetFunctionDefinitions());

        names.ShouldBe(new[] { FloorSkill, UngatedSkill }, ignoreOrder: true);
        names.ShouldNotContain(AdminOnlySkill);
        names.ShouldNotContain(SupervisorSkill);
        names.ShouldNotContain(AdminRoleGatedSkill);
    }

    [Test]
    public async Task FunctionDefinitions_ForASupervisor_ShowsTheSkillsItsRightsCover()
    {
        var names = NamesOf(await ControllerFor(Roles.Authorised).GetFunctionDefinitions());

        names.ShouldContain(SupervisorSkill);
        names.ShouldContain(FloorSkill);
        names.ShouldNotContain(AdminOnlySkill);
        names.ShouldNotContain(AdminRoleGatedSkill);
    }

    [Test]
    public async Task FunctionDefinitions_ForTheAdmin_ShowsTheWholeCatalogue()
    {
        var names = NamesOf(await ControllerFor(Roles.Admin).GetFunctionDefinitions());

        names.ShouldBe(
            new[] { AdminOnlySkill, SupervisorSkill, FloorSkill, UngatedSkill, AdminRoleGatedSkill },
            ignoreOrder: true);
    }

    [Test]
    public async Task FunctionDefinitions_AndFunctions_ShowTheSameSkillsToTheSameCaller()
    {
        var controller = ControllerFor(Roles.Authorised);

        var definitions = NamesOf(await controller.GetFunctionDefinitions());
        var functions = NamesOf(await controller.GetAvailableFunctions());

        definitions.ShouldBe(functions, ignoreOrder: true);
    }

    [Test]
    public async Task FunctionDefinitions_WithoutADefaultAgent_ReturnsAnEmptyList()
    {
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        var result = await ControllerFor(Roles.Admin).GetFunctionDefinitions();

        result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeAssignableTo<System.Collections.IEnumerable>()!.Cast<object>().ShouldBeEmpty();
    }
}
