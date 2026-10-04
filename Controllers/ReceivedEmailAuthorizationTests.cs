// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the role model of the received-email endpoints. Received mail is employee correspondence, so both
/// controllers answer only Admin and Authorised and the Planer floor (no role) does not reach them; erasing a
/// mail for good, testing the IMAP account and changing the folder layout stay Admin-only. The Admin-only set is
/// pinned exactly, so a new action cannot silently become stricter or a stricter one silently lose its gate.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Shouldly;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class ReceivedEmailAuthorizationTests
{
    private const string ReadRoles = $"{Roles.Admin},{Roles.Authorised}";

    private static readonly string[] ReceivedEmailAdminOnlyActions =
    [
        nameof(ReceivedEmailController.PermanentlyDelete),
        nameof(ReceivedEmailController.TestImapConnection),
    ];

    private static readonly string[] EmailFolderAdminOnlyActions =
    [
        nameof(EmailFoldersController.CreateFolder),
        nameof(EmailFoldersController.DeleteFolder),
    ];

    [TestCase(typeof(ReceivedEmailController))]
    [TestCase(typeof(EmailFoldersController))]
    public void Controller_AnswersOnlyAdminAndAuthorised(Type controllerType)
    {
        var attribute = controllerType.GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        attribute.ShouldNotBeNull($"{controllerType.Name} must not be open to the Planer floor");
        RolesOf(attribute!).ShouldBe(RolesOf(ReadRoles), ignoreOrder: true);
    }

    [TestCase(typeof(ReceivedEmailController), nameof(ReceivedEmailAdminOnlyActions))]
    [TestCase(typeof(EmailFoldersController), nameof(EmailFolderAdminOnlyActions))]
    public void ExactlyTheExpectedActions_AreAdminOnly(Type controllerType, string expectedField)
    {
        var expected = (string[])typeof(ReceivedEmailAuthorizationTests)
            .GetField(expectedField, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var adminOnly = ActionsOf(controllerType)
            .Where(m => m.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                .Any(a => RolesOf(a).SequenceEqual(new[] { Roles.Admin })))
            .Select(m => m.Name)
            .ToArray();

        adminOnly.ShouldBe(expected, ignoreOrder: true);
    }

    [TestCase(typeof(ReceivedEmailController))]
    [TestCase(typeof(EmailFoldersController))]
    public void NoAction_LoosensTheControllerGate(Type controllerType)
    {
        foreach (var action in ActionsOf(controllerType))
        {
            action.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true).ShouldBeNull(
                $"{controllerType.Name}.{action.Name} must not be anonymous");
        }
    }

    private static IEnumerable<MethodInfo> ActionsOf(Type controllerType)
        => controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any());

    private static string[] RolesOf(AuthorizeAttribute attribute) => RolesOf(attribute.Roles);

    private static string[] RolesOf(string? roles)
        => (roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
