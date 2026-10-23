// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that Klacks.Api never builds an in-memory IQueryable. An `Enumerable.Empty&lt;T&gt;().AsQueryable()` or
/// `list.AsQueryable()` looks like a query but has no EF Core provider, so the first CountAsync/ToListAsync on it
/// throws InvalidOperationException ("The source 'IQueryable' doesn't implement 'IAsyncEnumerable'"). Two such bugs
/// broke the group and the shift list without a date range (fixed in Api ad4dfd1ad and 1fe209b7e with
/// `query.Where(_ => false)`, which keeps the provider). On a real EF query AsQueryable is an identity call, so the
/// Api needs it nowhere: an empty result is `query.Where(_ => false)`, a DbSet is typed as `IQueryable&lt;T&gt;`.
/// The scan covers the whole Klacks.Api source except Migrations, bin and obj, with comments and string literals
/// blanked; it forbids AsQueryable and EnumerableQuery. No allowlist.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class InMemoryQueryableGuardTests
{
    private const int MinimumApiFiles = 4000;

    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
    ];

    private static readonly Regex InMemoryQueryable = new(
        @"\b(?:AsQueryable|EnumerableQuery)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Test]
    public void ApiSource_BuildsNoInMemoryQueryable()
    {
        var findings = ApiFiles()
            .SelectMany(file => Findings(file.Code).Select(line => $"{file.RelativePath}:{line}"))
            .ToList();

        findings.ShouldBeEmpty(
            "An in-memory IQueryable reaches EF Core async operators and throws. Use query.Where(_ => false) for an empty " +
            "result and type a DbSet as IQueryable<T> instead of calling AsQueryable:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Test]
    public void Scan_CoversTheApi()
    {
        ApiFiles().Count.ShouldBeGreaterThan(MinimumApiFiles, "The Api scan found too few files; the guard would pass vacuously.");
    }

    [TestCase("return Enumerable.Empty<Group>().AsQueryable();")]
    [TestCase("return Enumerable.Empty<Shift>().AsQueryable();")]
    [TestCase("var query = rows.ToList().AsQueryable();")]
    [TestCase("IQueryable<Shift> query = new EnumerableQuery<Shift>(rows);")]
    [TestCase("var query = Queryable.AsQueryable(rows);")]
    [TestCase("var query = context.Absence.AsQueryable();")]
    public void Scanner_FlagsEveryAsQueryableInCode(string line)
    {
        Findings(line).ShouldHaveSingleItem();
    }

    [Test]
    public void Scanner_IgnoresCommentsAndStrings()
    {
        const string source =
            "// list.AsQueryable() in a comment\n" +
            "/// <see cref=\"Queryable.AsQueryable\"/>\n" +
            "var text = \"AsQueryable in a string\";\n" +
            "return query.Where(_ => false);\n";

        Findings(source).ShouldBeEmpty();
    }

    private static List<string> Findings(string source)
    {
        return CSharpSourceBlanker.BlankCommentsAndStrings(source)
            .Split('\n')
            .Select((line, index) => (line, index))
            .Where(entry => InMemoryQueryable.IsMatch(entry.line))
            .Select(entry => $"{entry.index + 1}: {entry.line.Trim()}")
            .ToList();
    }

    private static List<(string RelativePath, string Code)> ApiFiles()
    {
        var root = RepositoryRootLocator.ApiProject;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ExcludedPathFragments.Any(path.Contains))
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)))
            .ToList();
    }
}
