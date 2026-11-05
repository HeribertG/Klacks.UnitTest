// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the JwtBearer events of Klacks.Api. Program.cs wires them through
/// JwtBearerOptions.EventsType = typeof(KlacksJwtBearerEvents); while EventsType is set, ASP.NET Core ignores any
/// JwtBearerOptions.Events instance, so a second "new JwtBearerEvents { OnMessageReceived = ... }" anywhere in the Api would
/// compile and silently do nothing (and before the switch, it would have bypassed the access-cookie source recording the
/// CSRF guard depends on). The scan is line-based, skips comment lines and fails on any construction of JwtBearerEvents or
/// assignment of one of its On... delegates. The allowlist is empty.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class JwtBearerEventsWiringGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const string ProgramFile = "Program.cs";
    private const string EventsTypeWiring = "EventsType = typeof(KlacksJwtBearerEvents)";
    private const int MinimumScannedFiles = 1000;
    private const string LineCommentPrefix = "//";

    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
    ];

    private static readonly Regex ForbiddenWiring = new(
        @"\bnew\s+JwtBearerEvents\b|\bOn(?:MessageReceived|TokenValidated|AuthenticationFailed|Challenge|Forbidden)\s*=(?!=)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Test]
    public void Program_WiresTheEventsThroughEventsType()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRootLocator.ApiProject, ProgramFile));

        program.ShouldContain(EventsTypeWiring);
    }

    [Test]
    public void Api_MustNotConstructJwtBearerEventsOrAssignTheirDelegates()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var report = new StringBuilder();
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            if (ExcludedPathFragments.Any(fragment => file.Contains(fragment, StringComparison.Ordinal)))
            {
                continue;
            }

            scannedFiles++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var match = ForbiddenWiring.Match(lines[i]);
                if (match.Success)
                {
                    report.AppendLine($"  {Path.GetRelativePath(apiRoot, file)}:{i + 1}: {match.Value}");
                }
            }
        }

        scannedFiles.ShouldBeGreaterThan(MinimumScannedFiles, "the scan did not read the real Api sources");
        report.Length.ShouldBe(
            0,
            "JwtBearer events are wired only through EventsType = typeof(KlacksJwtBearerEvents); an Events instance is " +
            $"ignored while EventsType is set. Extend KlacksJwtBearerEvents instead:{Environment.NewLine}{report}");
    }
}
