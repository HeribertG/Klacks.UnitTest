// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// The wizard job hubs stream employee names and plan details, so connecting to them (and with it JoinJob) needs the same
/// authorization as starting the job. Test 1 keeps each hub's class attribute equal to the class attribute of the
/// controller that starts the job (roles and policy), so the two cannot drift; only the controller's own attribute is read
/// (a BaseController adds a scheme-only attribute that changes no role). Test 2 evaluates the policy the hub's
/// attribute builds against real principals: a role that may not start the job is refused, the starting role is admitted.
/// Not covered: any permitted user may still join any job id (no per-job owner check).
/// </summary>
[TestFixture]
public class WizardJobHubAuthorizationTests
{
    private const string UserName = "planner";
    private const string AuthenticationType = "Test";

    public static IEnumerable<TestCaseData> HubsAndStartControllers()
    {
        yield return new TestCaseData(typeof(AutoWizardJobHub), typeof(AutoWizardController));
        yield return new TestCaseData(typeof(WizardJobHub), typeof(WizardController));
        yield return new TestCaseData(typeof(HarmonizerJobHub), typeof(HarmonizerController));
        yield return new TestCaseData(typeof(HolisticHarmonizerJobHub), typeof(HolisticHarmonizerController));
    }

    public static IEnumerable<Type> Hubs() => HubsAndStartControllers().Select(c => (Type)c.Arguments[0]!);

    [TestCaseSource(nameof(HubsAndStartControllers))]
    public void Hub_RequiresTheSameRolesAndPolicyAsTheStartController(Type hubType, Type controllerType)
    {
        var hub = hubType.GetCustomAttributes<AuthorizeAttribute>(inherit: false).Single();
        var controller = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: false).Single();

        controller.Roles.ShouldNotBeNullOrEmpty($"{controllerType.Name} no longer restricts by role; revisit {hubType.Name}");
        hub.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        hub.Roles.ShouldBe(controller.Roles);
        hub.Policy.ShouldBe(controller.Policy);
    }

    [TestCaseSource(nameof(Hubs))]
    public async Task Hub_RefusesARoleThatMayNotStartTheJob(Type hubType)
    {
        (await IsAuthorizedAsync(hubType, Roles.Authorised)).ShouldBeFalse();
        (await IsAuthorizedAsync(hubType)).ShouldBeFalse("a signed-in user without a role is the planner floor");
    }

    [TestCaseSource(nameof(Hubs))]
    public async Task Hub_AdmitsTheRoleThatStartsTheJob(Type hubType)
    {
        (await IsAuthorizedAsync(hubType, Roles.Admin)).ShouldBeTrue();
    }

    private static async Task<bool> IsAuthorizedAsync(Type hubType, params string[] roles)
    {
        var services = new ServiceCollection().AddLogging().AddAuthorizationCore().BuildServiceProvider();
        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, [hubType.GetCustomAttribute<AuthorizeAttribute>()!]);
        var claims = new List<Claim> { new(ClaimTypes.Name, UserName) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));

        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, policy!);
        return result.Succeeded;
    }
}
