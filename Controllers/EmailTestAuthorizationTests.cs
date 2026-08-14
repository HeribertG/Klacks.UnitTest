using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Email;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Microsoft.AspNetCore.Authorization;
using Shouldly;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class EmailTestAuthorizationTests
{
    [Test]
    public void TestImapConnection_ShouldRequireAdminRole()
    {
        var methodInfo = typeof(ReceivedEmailController)
            .GetMethod("TestImapConnection", BindingFlags.Public | BindingFlags.Instance);

        methodInfo.ShouldNotBeNull();

        var authorizeAttribute = methodInfo!.GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        authorizeAttribute.ShouldNotBeNull(
            "TestImapConnection reaches an arbitrary host with a server-held mailbox password");
        authorizeAttribute!.Roles.ShouldBe(Roles.Admin,
            "it must match its SMTP twin on GeneralSettingsController, which is Admin-only");
    }

    [Test]
    public void TestEmailConfiguration_ShouldRemainAdminOnly()
    {
        var controllerAttribute = typeof(GeneralSettingsController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        controllerAttribute.ShouldNotBeNull();
        controllerAttribute!.Roles.ShouldBe(Roles.Admin);
    }
}
