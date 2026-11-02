// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the rule "a reader that starts at the group items and reaches a shift looks at AnalyseToken": key shifts and
/// scenario clones have group items too, so a query without that check hands them to the real plan. The same goes for a group
/// query that includes the group items without a filter. Scans every .cs file of
/// Klacks.Api except migrations, entity configurations and build output with GroupItemShiftTokenScanner; its gaps are
/// documented there. Same pattern as MagicNumberGuardTests: files that already had such a query when the guard came in stand
/// in GroupItemShiftTokenGuardAllowlist with their exact count, a new file with a hit fails, a count that differs in either
/// direction fails, and so does an entry whose file is gone.
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class GroupItemShiftTokenGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const int MinimumScannedFiles = 1000;

    private static readonly string[] ExcludedDirectoryNames = ["Migrations", "Configurations", "bin", "obj"];

    private static readonly Lazy<(Dictionary<string, IReadOnlyList<int>> Hits, int ScannedFiles)> Scan = new(ScanApi);

    [Test]
    public void GroupItemQueries_ThatReachAShift_MustCheckTheToken()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scannedFiles} source files were scanned. The guard cannot have inspected the real source tree.");

        var violations = hits
            .Where(hit => !GroupItemShiftTokenGuardAllowlist.Entries.ContainsKey(hit.Key))
            .OrderBy(hit => hit.Key, StringComparer.Ordinal)
            .ToList();

        var report = new StringBuilder();
        foreach (var (path, lines) in violations)
        {
            report.AppendLine($"  {path}: {lines.Count} quer(ies) at line(s) {string.Join(", ", lines)}");
        }

        violations.ShouldBeEmpty(
            "A query that starts at the group items and reaches a shift must filter AnalyseToken, or key shifts and " +
            "scenario clones leave their world. Filter the query; do not add the file to the allowlist." +
            $"{Environment.NewLine}{report}{Environment.NewLine}Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Allowlist_MustMatchTheSourceExactly()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(MinimumScannedFiles);

        var stale = new StringBuilder();
        foreach (var (path, expectedCount) in GroupItemShiftTokenGuardAllowlist.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!hits.TryGetValue(path, out var lines))
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has no such query anymore (or is gone) - remove the entry.");
                continue;
            }

            if (lines.Count != expectedCount)
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has {lines.Count} at line(s) {string.Join(", ", lines)}.");
            }
        }

        stale.Length.ShouldBe(
            0,
            "GroupItemShiftTokenGuardAllowlist is out of date. It is shrink-only: lower or remove an entry after a cleanup; " +
            $"a count that grew must be fixed in the source.{Environment.NewLine}{stale}{Environment.NewLine}" +
            $"Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Scanner_FindsAChainThatReachesTheShiftWithoutTheToken()
    {
        const string source = """
            public class Sample
            {
                public object Read() => context.GroupItem
                    .Where(gi => gi.GroupId == id)
                    .Select(gi => gi.Shift!.Name)
                    .ToList();
            }
            """;

        GroupItemShiftTokenScanner.FindUntokenedQueryLines(source).ShouldBe(new[] { 3 });
    }

    [Test]
    public void Scanner_FindsAShiftIdRead_InTheSetFormAndInAQueryExpression()
    {
        const string source = """
            public class Sample
            {
                public object First() => _context.Set<GroupItem>().Select(gi => gi.ShiftId).ToList();

                public object Second() =>
                    (from gi in _context.GroupItem where gi.GroupId == id select gi.ShiftId).ToList();
            }
            """;

        GroupItemShiftTokenScanner.FindUntokenedQueryLines(source).ShouldBe(new[] { 3, 6 });
    }

    [Test]
    public void Scanner_AcceptsATokenCheck_AndAQueryThatNeverReachesAShift()
    {
        const string source = """
            public class Sample
            {
                public object Filtered() => context.GroupItem
                    .Where(gi => gi.AnalyseToken == null)
                    .Select(gi => gi.Shift!.Name)
                    .ToList();

                public object Helper() => ScenarioSourceFilters.WhereEitherWorld(context.Set<GroupItem>()
                        .Where(gi => gi.ShiftId != null), source)
                    .ToList();

                public object Clients() => context.GroupItem.Select(gi => gi.ClientId).ToList();
            }
            """;

        GroupItemShiftTokenScanner.FindUntokenedQueryLines(source).ShouldBeEmpty();
    }

    [Test]
    public void Scanner_FindsAPlainIncludeOfTheGroupItems_OnAGroupQuery()
    {
        const string source = """
            public class Sample
            {
                public object Read() => context.Group
                    .Include(gr => gr.GroupItems)
                    .ThenInclude(gi => gi.Client)
                    .ToList();
            }
            """;

        GroupItemShiftTokenScanner.FindUntokenedQueryLines(source).ShouldBe(new[] { 3 });
    }

    [Test]
    public void Scanner_AcceptsAFilteredIncludeOnAGroupQuery_AndAnIncludeOnAnotherRoot()
    {
        const string source = """
            public class Sample
            {
                public object Real() => context.Group
                    .Include(gr => gr.GroupItems.Where(gi => gi.AnalyseToken == null))
                    .ToList();

                public object Clients() => context.Group
                    .Include(gr => gr.GroupItems.Where(gi => !gi.ShiftId.HasValue && gi.ClientId.HasValue))
                    .ToList();

                public object OwnLinks() => context.Shift.Include(s => s.GroupItems).ToList();
            }
            """;

        GroupItemShiftTokenScanner.FindUntokenedQueryLines(source).ShouldBeEmpty();
    }

    private static (Dictionary<string, IReadOnlyList<int>> Hits, int ScannedFiles) ScanApi()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var hits = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath.Split('/').Intersect(ExcludedDirectoryNames, StringComparer.Ordinal).Any())
            {
                continue;
            }

            scannedFiles++;

            var lines = GroupItemShiftTokenScanner.FindUntokenedQueryLines(File.ReadAllText(file));
            if (lines.Count > 0)
            {
                hits[relativePath] = lines;
            }
        }

        return (hits, scannedFiles);
    }

    private static string RenderTable(Dictionary<string, IReadOnlyList<int>> hits)
    {
        var table = new StringBuilder();
        foreach (var (path, lines) in hits.OrderBy(hit => hit.Key, StringComparer.Ordinal))
        {
            table.AppendLine($"        [\"{path}\"] = {lines.Count}, // line(s) {string.Join(", ", lines)}");
        }

        return table.ToString();
    }
}
