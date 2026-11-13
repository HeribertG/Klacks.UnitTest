// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the rule "a reader of client memberships on the group items says which memberships it means": the regular ones
/// (RegularMembership.IsRegular), every membership of the real plan including borrowed days (OrTemporary) or those of a
/// period (OrTemporaryWithin). A query that tests AnalyseToken against null by hand takes the borrowed days along
/// without having decided to. Scans every .cs file of Klacks.Api except migrations, entity configurations and build
/// output with GroupItemMembershipPredicateScanner; its gaps are documented there (navigations of another root and
/// in-memory filters are not seen), and GroupItemMembershipPredicateGuardAllowlist names the readers behind those gaps
/// whose choice is still open, so a green run of this guard is no statement about them. Same pattern as
/// GroupItemShiftTokenGuardTests: files that had such a query when the
/// guard came in stand in GroupItemMembershipPredicateGuardAllowlist with their exact count, a new file with a hit fails,
/// a count that differs in either direction fails, and so does an entry whose file is gone.
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class GroupItemMembershipPredicateGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const int MinimumScannedFiles = 1000;

    private static readonly string[] ExcludedDirectoryNames = ["Migrations", "Configurations", "bin", "obj"];

    private static readonly Lazy<(Dictionary<string, IReadOnlyList<int>> Hits, int ScannedFiles)> Scan = new(ScanApi);

    [Test]
    public void ClientMembershipQueries_MustNameTheMembershipsTheyMean()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scannedFiles} source files were scanned. The guard cannot have inspected the real source tree.");

        var violations = hits
            .Where(hit => !GroupItemMembershipPredicateGuardAllowlist.Entries.ContainsKey(hit.Key))
            .OrderBy(hit => hit.Key, StringComparer.Ordinal)
            .ToList();

        var report = new StringBuilder();
        foreach (var (path, lines) in violations)
        {
            report.AppendLine($"  {path}: {lines.Count} test(s) at line(s) {string.Join(", ", lines)}");
        }

        violations.ShouldBeEmpty(
            "A client-membership query on the group items must use RegularMembership.IsRegular, OrTemporary or " +
            "OrTemporaryWithin instead of testing AnalyseToken against null by hand; a borrowed person is a member only on " +
            "the borrowed days and only for planning. Use the predicate; do not add the file to the allowlist." +
            $"{Environment.NewLine}{report}{Environment.NewLine}Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Allowlist_MustMatchTheSourceExactly()
    {
        var (hits, scannedFiles) = Scan.Value;

        scannedFiles.ShouldBeGreaterThan(MinimumScannedFiles);

        var stale = new StringBuilder();
        foreach (var (path, expectedCount) in GroupItemMembershipPredicateGuardAllowlist.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!hits.TryGetValue(path, out var lines))
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has no such test anymore (or is gone) - remove the entry.");
                continue;
            }

            if (lines.Count != expectedCount)
            {
                stale.AppendLine($"  {path}: listed with {expectedCount} but has {lines.Count} at line(s) {string.Join(", ", lines)}.");
            }
        }

        stale.Length.ShouldBe(
            0,
            "GroupItemMembershipPredicateGuardAllowlist is out of date. It is shrink-only: lower or remove an entry after a " +
            $"cleanup; a count that grew must be fixed in the source.{Environment.NewLine}{stale}{Environment.NewLine}" +
            $"Current table:{Environment.NewLine}{RenderTable(hits)}");
    }

    [Test]
    public void Scanner_FindsAHandWrittenTokenTest_InAChainAndInAQueryExpression()
    {
        const string source = """
            public class Sample
            {
                public object Chain() => context.GroupItem
                    .Where(gi => gi.ClientId != null && gi.AnalyseToken == null)
                    .Select(gi => gi.GroupId)
                    .ToList();

                public object Expression() =>
                    (from item in _context.Set<GroupItem>()
                     join membership in _context.Membership on item.ClientId equals membership.ClientId
                     where item.AnalyseToken == null
                     select item.GroupId).ToList();
            }
            """;

        GroupItemMembershipPredicateScanner.FindHandWrittenTokenTestLines(source).ShouldBe(new[] { 4, 11 });
    }

    [Test]
    public void Scanner_FindsTheOtherSpellingsOfTheNullTest_AndAScenarioOverlay()
    {
        const string source = """
            public class Sample
            {
                public object Pattern() => context.GroupItem
                    .Where(gi => gi.AnalyseToken is null && gi.Client != null)
                    .ToList();

                public object HasValue() => context.GroupItem
                    .Where(gi => !gi.AnalyseToken.HasValue)
                    .Select(gi => gi.ClientId)
                    .ToList();

                public object Reversed() => context.GroupItem.Where(gi => null == gi.AnalyseToken && gi.ClientId == id).ToList();

                public object Overlay() => context.GroupItem
                    .Where(gi => (gi.AnalyseToken == null || gi.AnalyseToken == token) && gi.ClientId != null)
                    .ToList();
            }
            """;

        GroupItemMembershipPredicateScanner.FindHandWrittenTokenTestLines(source).ShouldBe(new[] { 4, 8, 12, 15 });
    }

    [Test]
    public void Scanner_AcceptsTheNamedPredicates_AndATestOfAnExactToken()
    {
        const string source = """
            public class Sample
            {
                public object Regular() => context.GroupItem
                    .Where(gi => gi.ClientId == id)
                    .Where(RegularMembership.IsRegular)
                    .ToList();

                public object Window() =>
                    (from item in context.GroupItem.Where(RegularMembership.OrTemporary)
                     where item.ClientId != null
                     select item.GroupId).ToList();

                public object Scenario() => context.GroupItem
                    .Where(gi => gi.AnalyseToken == token && gi.ClientId != null)
                    .ToList();
            }
            """;

        GroupItemMembershipPredicateScanner.FindHandWrittenTokenTestLines(source).ShouldBeEmpty();
    }

    [Test]
    public void Scanner_LeavesShiftLinkQueries_AndTokenTestsOfOtherRowsInTheSameQuery()
    {
        const string source = """
            public class Sample
            {
                public object ShiftLinks() => context.GroupItem
                    .Where(gi => gi.ShiftId == shiftId && gi.AnalyseToken == null)
                    .ToList();

                public object WorksOfAGroup() =>
                    (from w in context.Work
                     where w.AnalyseToken == null && w.ClientId == id
                     join gi in context.GroupItem on w.ShiftId equals gi.ShiftId
                     where gi.AnalyseToken == null
                     select gi.GroupId).ToList();

                public object Nested() => context.Work
                    .Where(w => w.AnalyseToken == null && w.ClientId == id
                        && context.GroupItem.Any(gi => gi.ShiftId == w.ShiftId && gi.AnalyseToken == null))
                    .ToList();
            }
            """;

        GroupItemMembershipPredicateScanner.FindHandWrittenTokenTestLines(source).ShouldBeEmpty();
    }

    [Test]
    public void Scanner_DoesNotSeeANavigationOfAnotherRoot_TheDocumentedGap()
    {
        const string source = """
            public class Sample
            {
                public object Visible() => context.Client
                    .Where(client => client.GroupItems.Any(gi => gi.AnalyseToken == null && gi.ClientId != null))
                    .ToList();
            }
            """;

        GroupItemMembershipPredicateScanner.FindHandWrittenTokenTestLines(source).ShouldBeEmpty();
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

            var lines = GroupItemMembershipPredicateScanner.FindHandWrittenTokenTestLines(File.ReadAllText(file));
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
