// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for MessagingSetupController: returns the diagnosis report, and its attributes pin the JWT
/// scheme, restrict it to the Admin role and gate it on the messaging feature plugin.
/// </summary>
using System.Reflection;
using Klacks.Plugin.Contracts.Filters;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Models.Setup;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class MessagingSetupControllerTests
{
    [Test]
    public async Task GetDiagnosis_ReturnsOkWithReport()
    {
        var diagnostics = Substitute.For<IMessagingSetupDiagnosticsService>();
        var report = new MessagingSetupReport(
            [new SetupStep(SetupStepCodes.ProviderPresent, SetupStepStatus.ActionRequired, SetupStepDetails.NoProviderConfigured)],
            []);
        diagnostics.DiagnoseAsync(Arg.Any<CancellationToken>()).Returns(report);
        var sut = new MessagingSetupController(diagnostics);

        var result = await sut.GetDiagnosis(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeSameAs(report);
    }

    [Test]
    public void Controller_AuthorizePinsJwtSchemeAndAdminRole()
    {
        var authorize = typeof(MessagingSetupController).GetCustomAttributes<AuthorizeAttribute>().ToList();

        authorize.Count.ShouldBe(1);
        authorize[0].AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        authorize[0].Roles.ShouldBe(MessagingConstants.RoleAdmin);
    }

    [Test]
    public void Controller_IsGatedOnMessagingPluginAndRoutedUnderMessaging()
    {
        typeof(MessagingSetupController).GetCustomAttribute<RequireFeaturePluginAttribute>().ShouldNotBeNull();
        typeof(MessagingSetupController).GetCustomAttribute<RouteAttribute>()!.Template.ShouldBe("api/messaging");

        var action = typeof(MessagingSetupController).GetMethod(nameof(MessagingSetupController.GetDiagnosis))!;
        action.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("setup-diagnosis");
        action.GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull();
    }
}
