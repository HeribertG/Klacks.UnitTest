// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the assistant gate: the HTTP entry points that let a caller reach Klacksy must carry the
/// RequireAssistant policy. Without it CanUseAssistant would be a constant nobody enforces, and taking
/// the assistant away from a role would silently change nothing. A new assistant controller that
/// forgets the policy fails here.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AssistantEntryPointAuthorizationTests
{
    public static IEnumerable<Type> GatedAssistantControllers()
    {
        yield return typeof(ChatController);
        yield return typeof(SkillsController);
        yield return typeof(AgentPlansController);
    }

    [TestCaseSource(nameof(GatedAssistantControllers))]
    public void AssistantController_RequiresTheAssistantPolicy(Type controllerType)
    {
        var attribute = controllerType.GetCustomAttribute<AuthorizeAttribute>();

        attribute.ShouldNotBeNull($"{controllerType.Name} must carry an [Authorize] attribute.");
        attribute!.Policy.ShouldBe(
            AuthorizationPolicies.RequireAssistant,
            $"{controllerType.Name} reaches the assistant, so it must require the " +
            $"{AuthorizationPolicies.RequireAssistant} policy — otherwise CanUseAssistant is never enforced.");
    }

    [TestCaseSource(nameof(GatedAssistantControllers))]
    public void AssistantController_StillPinsTheJwtScheme(Type controllerType)
    {
        var attribute = controllerType.GetCustomAttribute<AuthorizeAttribute>();

        attribute!.AuthenticationSchemes.ShouldBe(
            JwtBearerDefaults.AuthenticationScheme,
            $"{controllerType.Name} must keep its explicit scheme — adding a policy must not drop it, " +
            "because AddIdentity overrides the runtime default to cookie auth.");
    }
}
