// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the one authorization decision the standing-approval handlers cannot make for themselves.
/// Granting an advance approval is the most far-reaching autonomy decision in the proactive subsystem -
/// it lets Klacksy change the schedule for days without anybody being asked per finding, under the
/// granting account's own rights - so the endpoint has to be Admin-only, exactly like
/// ProactiveGovernanceController, and has to keep its explicit JWT scheme, because AddIdentity overrides
/// the runtime default to cookie authentication. The generic ControllerAuthorizeSchemeGuardTests would
/// catch a missing scheme but not a widened role, which is the mistake that would matter here.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ProactiveStandingApprovalsControllerAuthorizationTests
{
    [Test]
    public void TheController_IsAdminOnly_AndPinsTheJwtScheme()
    {
        var attribute = typeof(ProactiveStandingApprovalsController).GetCustomAttribute<AuthorizeAttribute>();

        attribute.ShouldNotBeNull("Granting unattended autonomy must never be reachable without authorization.");
        attribute!.Roles.ShouldBe(
            Roles.Admin,
            "A standing approval decides what Klacksy may do without being asked, which is the same "
            + "decision ProactiveGovernanceController guards as Admin-only.");
        attribute.AuthenticationSchemes.ShouldBe(
            JwtBearerDefaults.AuthenticationScheme,
            "Without the explicit scheme every JWT request is sent to cookie authentication instead.");
    }

    /// <summary>
    /// The granting account is the identity every execution under the grant borrows, so it must come from
    /// the authenticated principal. A request DTO that carried a user id would let an administrator grant
    /// autonomy in somebody else's name.
    /// </summary>
    [Test]
    public void TheGrantRequest_CarriesNoUserId()
    {
        var properties = typeof(Klacks.Api.Application.DTOs.Assistant.GrantStandingApprovalRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        properties.ShouldNotContain("GrantedByUserId");
        properties.ShouldNotContain("UserId");
    }
}
