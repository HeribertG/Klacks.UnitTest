// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ControllerAuthorizeSchemeGuardTests and ForbidChallengeSchemeGuardTests only look at the Klacks.Api assembly and
/// its Presentation sources, so the messaging plugin's controllers fell through both. Same rules here: every
/// plugin controller either pins the JWT scheme on its class-level [Authorize] (method-level role attributes are
/// combined with it) or is explicitly anonymous, and no plugin controller answers with a scheme-less
/// Forbid()/Challenge(), which AddIdentity turns into a cookie redirect instead of a 403.
/// </summary>

using System.Reflection;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class MessagingPluginControllerAuthGuardTests
{
    private const string PluginProjectDirectory = "Klacks.Plugin.Messaging";
    private const string PresentationDirectory = "Presentation";
    private const string SourceFilePattern = "*.cs";
    private const int MinimumControllers = 5;

    private static readonly string[] ForbiddenPatterns =
    [
        "Forbid()",
        "Challenge()",
        "ForbidResult()",
        "ChallengeResult()"
    ];

    [Test]
    public void EveryPluginController_PinsTheJwtSchemeOrIsExplicitlyAnonymous()
    {
        var controllers = typeof(MessagingController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        controllers.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumControllers, "Too few plugin controllers found; the guard is not looking at the real assembly.");

        var violations = controllers
            .Where(t => t.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) == null)
            .Where(t => !t.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Any(a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme))
            .Select(t => t.Name)
            .ToList();

        violations.ShouldBeEmpty(
            "These plugin controllers neither pin the JWT scheme on a class-level [Authorize] nor are anonymous; " +
            "AddIdentity makes cookie authentication the default, so every JWT request would get a 401.");
    }

    [Test]
    public void PluginPresentation_MustNotUseSchemelessForbidOrChallenge()
    {
        var presentation = RepositoryRootLocator.RequireDirectory(PluginProjectDirectory, PresentationDirectory);
        var files = Directory.EnumerateFiles(presentation, SourceFilePattern, SearchOption.AllDirectories).ToList();
        files.Count.ShouldBeGreaterThanOrEqualTo(MinimumControllers);

        var violations = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(x => ForbiddenPatterns.Any(p => x.line.Contains(p, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.index + 1}")
            .ToList();

        violations.ShouldBeEmpty("Use Forbid(JwtBearerDefaults.AuthenticationScheme) or StatusCode(403).");
    }
}
