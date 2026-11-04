// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The feature plugin list and detail endpoints require an authenticated JWT user (they were anonymous):
/// every signed-in user may read them, because the navigation shows plugin pages, but an anonymous caller
/// learns nothing about installed plugins.
/// </summary>
using System.Reflection;
using Klacks.Api.Presentation.Controllers.Plugins;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class FeaturePluginControllerAuthorizationTests
{
    [TestCase(nameof(FeaturePluginController.GetAllPlugins))]
    [TestCase(nameof(FeaturePluginController.GetPlugin))]
    public void ReadEndpoint_RequiresAuthenticatedJwtUserWithoutRole(string methodName)
    {
        var method = typeof(FeaturePluginController).GetMethod(methodName)!;

        method.GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull();
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        authorize.ShouldNotBeNull();
        authorize!.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        authorize.Roles.ShouldBeNull();
    }
}
