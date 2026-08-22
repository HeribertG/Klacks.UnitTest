// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard against scheme-less Forbid() / Challenge() in the presentation layer of
/// Klacks.Api. Program.cs registers JwtBearer as the default scheme, but the later AddIdentity call
/// moves the default to cookie authentication. A parameterless Forbid() therefore resolves the cookie
/// forbid handler, which answers a denied request with a 302 redirect to a login page instead of a
/// 403 — the SPA sees a redirect, not a permission error. The same applies to Challenge() and to the
/// hand-built ForbidResult / ChallengeResult. Pinning the scheme, Forbid(JwtBearerDefaults
/// .AuthenticationScheme), makes the answer independent of registration order. This is the same
/// AddIdentity trap the [Authorize] rule in .claude/rules/code-policies.md describes, one level down.
///
/// A source scan is used deliberately: the violation is a method call inside a method body, and
/// reflection sees only signatures and attributes — the SignalR guard in
/// Infrastructure/Hubs/HubAuthorizationTests.cs can use reflection because it checks an attribute.
/// Reading the call sites would mean parsing IL, which is far more fragile than matching four
/// literals.
///
/// Scope note — what this guard does NOT cover:
/// - StatusCode(403) / StatusCodes.Status403Forbidden written by hand. That is a correct remedy, not
///   a violation, so it is not flagged; it is also not enforced as an alternative.
/// - Whitespace or comment variants such as "Forbid( )" or "Forbid(/*x*/)". The literals are matched
///   verbatim, so a deliberately reformatted call slips through.
/// - Helper methods outside Presentation/ that return a scheme-less result. Today every
///   ControllerBase descendant lives under Presentation/, which the second test keeps true, but a
///   helper in another layer returning IActionResult is invisible here.
/// - Controllers in separate projects, in particular Klacks.Plugin.Messaging, which is not part of
///   the scanned assembly or directory tree.
/// - The [Authorize]-without-scheme gap on controllers noted in code-policies.md. That is a different
///   check (attribute, not call site) and remains open — this guard does not close it.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ForbidChallengeSchemeGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string PresentationDirectory = "Presentation";
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const int MinimumScannedFiles = 100;
    private const string PinnedForbidPattern = "Forbid(JwtBearerDefaults.AuthenticationScheme)";

    private static readonly string[] ForbiddenPatterns =
    [
        "Forbid()",
        "Challenge()",
        "ForbidResult()",
        "ChallengeResult()"
    ];

    private static readonly Regex ClassDeclaration =
        new(@"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    [Test]
    public void PresentationLayer_MustNotUseSchemelessForbidOrChallenge()
    {
        var scan = ScanPresentationLayer();

        scan.ScannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scan.ScannedFiles} source files were scanned. The guard cannot have inspected the " +
            "real presentation layer, so a green result would be meaningless.");

        scan.PinnedOccurrences.ShouldBeGreaterThan(
            0,
            $"The scan found no '{PinnedForbidPattern}' anywhere. Either every call site was removed " +
            "or the scan is not reading the real controller sources — in both cases an empty violation " +
            "list proves nothing.");

        var report = new StringBuilder();
        foreach (var violation in scan.Violations)
        {
            report.AppendLine($"  {violation.File}:{violation.Line} -> {violation.Pattern}");
        }

        scan.Violations.ShouldBeEmpty(
            "Forbid() and Challenge() without an explicit scheme resolve the runtime default, which " +
            "AddIdentity overrides to cookie authentication: the caller gets a 302 redirect instead of " +
            $"a 403. Use {PinnedForbidPattern} — or state the status code outright with " +
            $"StatusCode(StatusCodes.Status403Forbidden).{Environment.NewLine}{report}");
    }

    [Test]
    public void AllControllers_MustLiveInTheScannedDirectory()
    {
        var scan = ScanPresentationLayer();

        var controllerTypes = typeof(Klacks.Api.Presentation.Controllers.UserBackend.BaseController).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        controllerTypes.ShouldNotBeEmpty(
            "No ControllerBase descendant was found in the Klacks.Api assembly. The guard would be " +
            "vacuously green.");

        var unscanned = controllerTypes
            .Where(t => !scan.DeclaredClassNames.Contains(t.Name))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        unscanned.ShouldBeEmpty(
            $"These controllers have no source file under {ApiProjectDirectory}/{PresentationDirectory}, " +
            "so the scheme-less Forbid()/Challenge() scan never sees them. Either move the controller " +
            "back under the presentation layer or widen the scanned directories of this guard: " +
            string.Join(", ", unscanned));
    }

    private static ScanResult ScanPresentationLayer()
    {
        var apiRoot = LocateApiProject();
        var absoluteDirectory = Path.Combine(apiRoot, PresentationDirectory);

        if (!Directory.Exists(absoluteDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Guarded directory '{PresentationDirectory}' does not exist under '{apiRoot}'. " +
                "The guard would silently pass, so this is treated as a failure.");
        }

        var violations = new List<(string File, int Line, string Pattern)>();
        var declaredClassNames = new HashSet<string>(StringComparer.Ordinal);
        var scannedFiles = 0;
        var pinnedOccurrences = 0;

        foreach (var file in Directory.EnumerateFiles(absoluteDirectory, SourceFilePattern, SearchOption.AllDirectories))
        {
            scannedFiles++;
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in ClassDeclaration.Matches(line))
                {
                    declaredClassNames.Add(match.Groups["name"].Value);
                }

                if (line.Contains(PinnedForbidPattern, StringComparison.Ordinal))
                {
                    pinnedOccurrences++;
                }

                foreach (var pattern in ForbiddenPatterns)
                {
                    if (line.Contains(pattern, StringComparison.Ordinal))
                    {
                        violations.Add((relative, i + 1, pattern));
                    }
                }
            }
        }

        return new ScanResult(violations, declaredClassNames, scannedFiles, pinnedOccurrences);
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, PresentationDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }

    private sealed record ScanResult(
        IReadOnlyList<(string File, int Line, string Pattern)> Violations,
        IReadOnlySet<string> DeclaredClassNames,
        int ScannedFiles,
        int PinnedOccurrences);
}
