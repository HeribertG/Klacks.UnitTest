// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Klacks.UnitTest.Infrastructure.Hubs;

/// <summary>
/// The wizard job hubs stream employee names and plan details, so connecting to them (and with it JoinJob) needs the same
/// authorization as starting the job. Test 1 keeps each hub's class attribute equal to the class attribute of the
/// controller that starts the job (roles and policy), so the two cannot drift; only the controller's own attribute is read
/// (a BaseController adds a scheme-only attribute that changes no role). The other tests evaluate the policy the hub's
/// attribute builds against real principals: the three hubs of the developer dropdown admit only Admin; the AutoWizard hub
/// (normal autofill, planning work since the owner rule of 2026-11-07) admits every signed-in user holding the planning right
/// CanEditSchedule (today every role and the planner floor), and its JoinJob is bounded per job by IAutoWizardJobAccessGuard (pinned in AutoWizardJobHubJoinTests). Not covered: an Admin may join any
/// job id of the three admin hubs (no per-job owner check).
/// </summary>
[TestFixture]
public class WizardJobHubAuthorizationTests
{
    public static IEnumerable<TestCaseData> HubsAndStartControllers()
    {
        yield return new TestCaseData(typeof(AutoWizardJobHub), typeof(AutoWizardController));
        yield return new TestCaseData(typeof(WizardJobHub), typeof(WizardController));
        yield return new TestCaseData(typeof(HarmonizerJobHub), typeof(HarmonizerController));
        yield return new TestCaseData(typeof(HolisticHarmonizerJobHub), typeof(HolisticHarmonizerController));
    }

    public static IEnumerable<Type> Hubs() => HubsAndStartControllers().Select(c => (Type)c.Arguments[0]!);

    public static IEnumerable<Type> AdminHubs() => Hubs().Where(hub => hub != typeof(AutoWizardJobHub));

    [TestCaseSource(nameof(HubsAndStartControllers))]
    public void Hub_RequiresTheSameRolesAndPolicyAsTheStartController(Type hubType, Type controllerType)
    {
        var hub = hubType.GetCustomAttributes<AuthorizeAttribute>(inherit: false).Single();
        var controller = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: false).Single();

        hub.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        hub.Roles.ShouldBe(controller.Roles);
        hub.Policy.ShouldBe(controller.Policy);
    }

    [TestCaseSource(nameof(AdminHubs))]
    public async Task AdminHub_RefusesARoleThatMayNotStartTheJob(Type hubType)
    {
        (await HubAuthorizationEvaluator.IsAuthorizedAsync(hubType, Roles.Authorised)).ShouldBeFalse();
        (await HubAuthorizationEvaluator.IsAuthorizedAsync(hubType)).ShouldBeFalse("a signed-in user without a role is the planner floor");
    }

    [TestCaseSource(nameof(Hubs))]
    public async Task Hub_AdmitsTheRoleThatStartsTheJob(Type hubType)
    {
        (await HubAuthorizationEvaluator.IsAuthorizedAsync(hubType, Roles.Admin)).ShouldBeTrue();
    }

    [Test]
    public async Task AutoWizardJobHub_AdmitsEverySignedInPlanner()
    {
        (await HubAuthorizationEvaluator.IsAuthorizedAsync(typeof(AutoWizardJobHub), Roles.Authorised)).ShouldBeTrue();
        (await HubAuthorizationEvaluator.IsAuthorizedAsync(typeof(AutoWizardJobHub), Roles.User)).ShouldBeTrue();
        (await HubAuthorizationEvaluator.IsAuthorizedAsync(typeof(AutoWizardJobHub)))
            .ShouldBeTrue("a signed-in user without a role is the planner floor");
    }
}
