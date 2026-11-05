// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// api/backend/Groups/refresh repairs the nested-set values of the group tree, so it changes data. Since the Ui
/// authenticates with the HttpOnly session cookie, the global cookie guard only demands the CSRF proof on unsafe
/// methods; a GET would let any page trigger the repair with the user's cookie. Pins that the action is a POST only.
/// </summary>

using System.Reflection;
using Klacks.Api.Presentation.Controllers.UserBackend.Associations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Klacks.UnitTest.Controllers.Groups;

[TestFixture]
public class GroupsControllerRefreshVerbTests
{
    private const string RefreshRoute = "refresh";

    [Test]
    public void Refresh_IsRoutedAsPostOnly()
    {
        var method = typeof(GroupsController).GetMethod(nameof(GroupsController.Refresh))!;

        var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToList();

        verbs.Count.ShouldBe(1);
        verbs[0].ShouldBeOfType<HttpPostAttribute>();
        verbs[0].Template.ShouldBe(RefreshRoute);
    }
}
