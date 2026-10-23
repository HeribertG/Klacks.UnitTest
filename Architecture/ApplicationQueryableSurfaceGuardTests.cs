// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that the Application layer never composes a database query (.claude/rules/backend-architecture.md; round 5 of
/// the clean-architecture audit): it reads through materialising repositories and readers, so it neither names an
/// IQueryable nor holds an interface that hands one out. Two checks, both without an allowlist:
/// - the Application source (comments and string literals blanked) names neither IQueryable, IOrderedQueryable nor
///   AsQueryable;
/// - no interface the Application source names by its simple name (Domain, Application or a KnowledgeIndex contract;
///   Infrastructure and Presentation are out of reach by LayerDependencyGuardTests) has a member - inherited ones
///   included - whose signature carries a queryable anywhere: return type, parameter, generic argument, tuple element,
///   delegate or array. A `var q = repository.GetQuery()` therefore fails here although the Application file never
///   spells the type.
/// Composable query seams that only Infrastructure uses live on Infrastructure interfaces (IShiftScheduleService,
/// IClientBaseQueryService, IClientGroupFilterService, IClientSearchFilterService) that derive from the narrow Domain
/// interface Application reads through. Microsoft.EntityFrameworkCore in Application is forbidden by
/// LayerDependencyGuardTests.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Domain.Interfaces;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ApplicationQueryableSurfaceGuardTests
{
    private const string ApplicationFolder = "Application";
    private const string InfrastructureNamespace = "Klacks.Api.Infrastructure";
    private const string PresentationNamespace = "Klacks.Api.Presentation";
    private const int MinimumApplicationFiles = 1000;
    private const int MinimumScannedInterfaces = 500;

    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
    ];

    private static readonly Regex QueryableName = new(
        @"\b(?:IQueryable|IOrderedQueryable|AsQueryable)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Identifier = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Test]
    public void ApplicationSource_NamesNoQueryable()
    {
        var findings = ApplicationFiles()
            .SelectMany(file => file.Lines
                .Select((line, index) => (line, index))
                .Where(entry => QueryableName.IsMatch(entry.line))
                .Select(entry => $"{file.RelativePath}:{entry.index + 1}: {entry.line.Trim()}"))
            .ToList();

        findings.ShouldBeEmpty(
            "Application composes no database query. Read through a materialising repository or reader method " +
            $"(criteria record + Task<IReadOnlyList<T>>) instead:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Test]
    public void InterfacesTheApplicationNames_HandOutNoQueryable()
    {
        var queryableInterfaces = QueryableInterfacesBySimpleName();
        var findings = ApplicationFiles()
            .SelectMany(file => Identifier.Matches(file.Code)
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .Where(queryableInterfaces.ContainsKey)
                .Select(name => $"{file.RelativePath} -> {name} ({string.Join(", ", queryableInterfaces[name])})"))
            .OrderBy(finding => finding, StringComparer.Ordinal)
            .ToList();

        findings.ShouldBeEmpty(
            "These interfaces hand out an IQueryable and must not be reachable from Application. Give Application a " +
            "narrow Domain interface with materialising reads and move the query member to an Infrastructure interface " +
            $"that derives from it:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Test]
    public void Scan_CoversTheApplicationLayerAndTheApiInterfaces()
    {
        ApplicationFiles().Count.ShouldBeGreaterThan(MinimumApplicationFiles, "The Application scan found too few files.");
        ScannedInterfaces().Count.ShouldBeGreaterThan(
            MinimumScannedInterfaces, "The reflection scan found too few Domain/Application interfaces; the guard would pass vacuously.");
    }

    [Test]
    public void SignatureCheck_FindsQueryablesInEveryPosition()
    {
        HandsOutQueryable(typeof(IProbeTupleInTask)).ShouldBeTrue();
        HandsOutQueryable(typeof(IProbeDelegateParameter)).ShouldBeTrue();
        HandsOutQueryable(typeof(IProbeOrderedProperty)).ShouldBeTrue();
        HandsOutQueryable(typeof(IProbeInheritsQueryable)).ShouldBeTrue();
        HandsOutQueryable(typeof(IProbeArrayOfQueryables)).ShouldBeTrue();
        HandsOutQueryable(typeof(IProbeMaterialising)).ShouldBeFalse();
    }

    [Test]
    public void SourceCheck_IgnoresCommentsAndStrings_ButSeesCode()
    {
        const string source =
            "// IQueryable in a comment\n" +
            "var text = \"IQueryable in a string\";\n" +
            "/// <see cref=\"IQueryable{T}\"/>\n" +
            "var query = list.AsQueryable();\n";

        var blanked = CSharpSourceBlanker.BlankCommentsAndStrings(source).Split('\n');

        blanked.Count(line => QueryableName.IsMatch(line)).ShouldBe(1);
        QueryableName.IsMatch(blanked[3]).ShouldBeTrue();
    }

    private static List<Type> ScannedInterfaces()
    {
        return typeof(IUnitOfWork).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace != null)
            .Where(type => !type.Namespace!.StartsWith(InfrastructureNamespace, StringComparison.Ordinal)
                           && !type.Namespace.StartsWith(PresentationNamespace, StringComparison.Ordinal))
            .ToList();
    }

    private static Dictionary<string, List<string>> QueryableInterfacesBySimpleName()
    {
        return ScannedInterfaces()
            .Select(type => (Name: QueryableSignatureInspector.SimpleName(type), Members: QueryableMembers(type)))
            .Where(entry => entry.Members.Count > 0)
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.SelectMany(entry => entry.Members).Distinct().ToList(), StringComparer.Ordinal);
    }

    private static bool HandsOutQueryable(Type interfaceType) => QueryableMembers(interfaceType).Count > 0;

    private static List<string> QueryableMembers(Type interfaceType)
    {
        return new[] { interfaceType }
            .Concat(interfaceType.GetInterfaces())
            .SelectMany(QueryableSignatureInspector.ExposedQueryableMembers)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<SourceFile> ApplicationFiles()
    {
        var root = RepositoryRootLocator.ApiProject;
        var applicationRoot = Path.Combine(root, ApplicationFolder);
        return Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ExcludedPathFragments.Any(path.Contains))
            .Select(path =>
            {
                var code = CSharpSourceBlanker.BlankCommentsAndStrings(File.ReadAllText(path));
                return new SourceFile(Path.GetRelativePath(root, path), code, code.Split('\n'));
            })
            .ToList();
    }

    private sealed record SourceFile(string RelativePath, string Code, string[] Lines);

    public interface IProbeTupleInTask
    {
        Task<(IQueryable<string> Rows, int Total)> Read();
    }

    public interface IProbeDelegateParameter
    {
        void Use(Func<Guid, IQueryable<object>> fetch);
    }

    public interface IProbeOrderedProperty
    {
        IOrderedQueryable<int> Sorted { get; }
    }

    public interface IProbeInheritsQueryable : IProbeTupleInTask
    {
    }

    public interface IProbeArrayOfQueryables
    {
        void Use(IQueryable<int>[] parts);
    }

    public interface IProbeMaterialising
    {
        Task<IReadOnlyList<string>> Read(IEnumerable<Guid> ids);
    }
}
