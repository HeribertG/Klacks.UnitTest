// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the rule "no magic numbers, use constants" (.claude/rules/code-policies.md) for the business logic of
/// Klacks.Api: every .cs file under Domain/ and Application/, except any directory named Constants. Migrations,
/// Presentation, Infrastructure, plugins and tests are out of scope for now.
///
/// A Roslyn syntax scan (MagicNumberLiteralScanner) rather than a text scan: literals inside strings, comments,
/// attributes, enum members and const/static readonly initializers must not count, and only the syntax tree
/// tells them apart reliably. The exemptions are documented on the scanner. The scanner needs the direct
/// Microsoft.CodeAnalysis.CSharp package reference of this project (transitively it only arrives without compile assets).
///
/// Files that already contained magic numbers when the guard was introduced (2026-10-23) are listed in
/// MagicNumberGuardAllowlist with their exact count. The list is shrink-only: a new file with a hit fails,
/// an allowlisted file whose count differs in either direction fails, and so does an entry whose file is gone.
/// The failure message prints the current table as a C# initializer, so a cleanup can copy the lowered number.
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class MagicNumberGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const string ExcludedDirectoryName = "Constants";
    private const int MinimumScannedFiles = 1000;

    private static readonly string[] GuardedDirectories = ["Domain", "Application"];

    private static readonly Lazy<(Dictionary<string, IReadOnlyList<int>> Hits, int ScannedFiles)> Scan =
        new(ScanGuardedDirectories);

    [Test]
    public void BusinessLogic_MustNotIntroduceMagicNumbers()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scannedFiles} source files were scanned. The guard cannot have inspected the real " +
            "source tree, so a green result would be meaningless.");

        var violations = hits
            .Where(hit => !MagicNumberGuardAllowlist.Entries.ContainsKey(hit.Key))
            .OrderBy(hit => hit.Key, StringComparer.Ordinal)
            .ToList();

        var report = new StringBuilder();
        foreach (var (path, lines) in violations)
        {
            report.AppendLine($"  {path}: {lines.Count} literal(s) at line(s) {string.Join(", ", lines)}");
        }

        violations.ShouldBeEmpty(
            "Numeric literals other than 0, 1 and -1 are magic numbers in Domain/ and Application/. Name the " +
            "value as a const or static readonly field at its owning class, or in a Constants file of the layer " +
            "(unit in the name, e.g. MaxNameLength, EarthRadiusKm). Do not add the file to " +
            $"MagicNumberGuardAllowlist.{Environment.NewLine}{report}{Environment.NewLine}" +
            $"Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Allowlist_MustMatchTheSourceExactly()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(MinimumScannedFiles);

        var stale = new StringBuilder();
        foreach (var (path, expectedCount) in MagicNumberGuardAllowlist.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!hits.TryGetValue(path, out var lines))
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has no magic number anymore (or is gone) - remove the entry.");
                continue;
            }

            if (lines.Count != expectedCount)
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has {lines.Count} " +
                                 $"at line(s) {string.Join(", ", lines)}.");
            }
        }

        stale.Length.ShouldBe(
            0,
            "MagicNumberGuardAllowlist is out of date. It is shrink-only: lower or remove an entry after a " +
            "cleanup; a count that grew must be fixed in the source, not raised here." +
            $"{Environment.NewLine}{stale}{Environment.NewLine}Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Scanner_CountsOnlyUnnamedLiteralsOtherThanZeroAndOne()
    {
        const string source = """
            using System.ComponentModel.DataAnnotations;
            public enum Level { Low = 5, High = 10 }
            public class Sample
            {
                private const int Named = 7;
                private static readonly int[] Table = [2, 3, 4];
                private readonly int _instance = 9;
                [Range(2, 9)]
                public int Value { get; set; }
                public double Compute(int x)
                {
                    const int localNamed = 3;
                    var text = "42 and 43";
                    var trivial = 0 + 1 - 1 + 0.0 + 1.0 + 0m + 1L;
                    return x * 42 + 2.5 + localNamed;
                }
            }
            """;

        var lines = MagicNumberLiteralScanner.FindMagicNumberLines(source);

        lines.ShouldBe(new[] { 7, 15, 15 });
    }

    private static (Dictionary<string, IReadOnlyList<int>> Hits, int ScannedFiles) ScanGuardedDirectories()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var hits = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        var scannedFiles = 0;

        foreach (var guardedDirectory in GuardedDirectories)
        {
            var absoluteDirectory = Path.Combine(apiRoot, guardedDirectory);
            if (!Directory.Exists(absoluteDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Guarded directory '{guardedDirectory}' does not exist under '{apiRoot}'. " +
                    "The guard would silently pass, so this is treated as a failure.");
            }

            foreach (var file in Directory.EnumerateFiles(absoluteDirectory, SourceFilePattern, SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relativePath.Split('/').Contains(ExcludedDirectoryName, StringComparer.Ordinal))
                {
                    continue;
                }

                scannedFiles++;

                var lines = MagicNumberLiteralScanner.FindMagicNumberLines(File.ReadAllText(file));
                if (lines.Count > 0)
                {
                    hits[relativePath] = lines;
                }
            }
        }

        return (hits, scannedFiles);
    }

    private static string RenderTable(Dictionary<string, IReadOnlyList<int>> hits)
    {
        var table = new StringBuilder();
        foreach (var (path, lines) in hits.OrderBy(hit => hit.Key, StringComparer.Ordinal))
        {
            table.AppendLine($"        [\"{path}\"] = {lines.Count},");
        }

        return table.ToString();
    }
}
