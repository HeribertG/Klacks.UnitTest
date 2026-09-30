// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the dependency of the Klacks.Api domain layer on the Klacks.ScheduleOptimizer project. The
/// domain may only use the pure shared calculations (Common.RestDays, the weekly rest-day definition
/// shared by the schedule check and the wizards) and the bitmap vocabulary persisted with softenings
/// (Harmonizer.Bitmap). Anything else from the optimizer - engines, operators, models of a run - must
/// stay outside the domain. A source scan over Domain/**, because a using directive or a fully
/// qualified name is exactly the violation and both are visible in the text.
/// </summary>

using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class DomainScheduleOptimizerReferenceGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string DomainDirectory = "Domain";
    private const string SourceFilePattern = "*.cs";
    private const int MinimumScannedFiles = 100;

    private static readonly string[] AllowedNamespaces =
    [
        "Klacks.ScheduleOptimizer.Common.RestDays",
        "Klacks.ScheduleOptimizer.Harmonizer.Bitmap",
    ];

    private static readonly Regex OptimizerReference = new(
        @"\bKlacks\.ScheduleOptimizer(?:\.[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Test]
    public void DomainLayer_ReferencesOnlyTheAllowedOptimizerNamespaces()
    {
        var domainRoot = Path.Combine(LocateApiProject(), DomainDirectory);
        var files = Directory.EnumerateFiles(domainRoot, SourceFilePattern, SearchOption.AllDirectories).ToList();
        var violations = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in OptimizerReference.Matches(lines[index]))
                {
                    if (!IsAllowed(match.Value))
                    {
                        violations.Add($"{Path.GetRelativePath(domainRoot, file)}:{index + 1}: {match.Value}");
                    }
                }
            }
        }

        files.Count.ShouldBeGreaterThan(MinimumScannedFiles, "The domain scan found too few files; the guard would pass vacuously.");
        violations.ShouldBeEmpty(
            "The domain layer may only reference Klacks.ScheduleOptimizer.Common.RestDays and " +
            "Klacks.ScheduleOptimizer.Harmonizer.Bitmap: " + string.Join(", ", violations));
    }

    private static bool IsAllowed(string reference)
        => AllowedNamespaces.Any(allowed =>
            reference.StartsWith(allowed, StringComparison.Ordinal)
            && (reference.Length == allowed.Length || reference[allowed.Length] == '.'));

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, DomainDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
