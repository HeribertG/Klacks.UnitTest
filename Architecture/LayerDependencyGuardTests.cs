// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the dependency direction of the Klacks.Api layers (.claude/rules/backend-architecture.md):
/// Domain depends on nothing, Application only on Domain, Infrastructure on Application and Domain,
/// Presentation on Application and Domain. On top of the table: DataBaseContext stays inside
/// Infrastructure, the Domain layer does not use Microsoft.EntityFrameworkCore, and the host Domain does
/// not reference the KnowledgeIndex module (the module itself consumes the host Domain, so the reverse
/// edge would be a cycle).
///
/// Klacks.Api is one assembly, so the compiler cannot enforce any of this; the layers are folders and
/// namespaces. A source scan sees exactly the violation: a using directive, a fully qualified name, or a
/// relative-qualified name such as "Application.Constants.Settings.X" that resolves through the
/// enclosing Klacks.Api namespace. Comments (including XML-doc crefs) and string literals are blanked
/// before matching, so prose and prompt text never count.
///
/// Scope: the four host layer folders plus GlobalUsings.cs (checked with the Domain rules, because a
/// global using reaches every layer). Not scanned: Program.cs and the *ServiceCollectionExtensions DI
/// files (composition roots wire every layer by design), Properties/, Migrations/, and the KnowledgeIndex
/// module's own internal layering.
///
/// Violations that existed when the guard was introduced (2026-10-20) are listed in KnownViolations,
/// keyed "relative path -> referenced name" (the qualified namespace or type, DataBaseContext, or
/// Microsoft.EntityFrameworkCore), so a second forbidden reference in an allowlisted file still fails. The
/// list is shrink-only: a new violation fails, and so does an entry that no longer occurs - remove it in the
/// same change that fixes the file.
/// </summary>

using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class LayerDependencyGuardTests
{
    private const string Domain = "Domain";
    private const string Application = "Application";
    private const string Infrastructure = "Infrastructure";
    private const string Presentation = "Presentation";
    private const string KnowledgeIndex = "KnowledgeIndex";

    // The seed classes live in Infrastructure/Persistence/Seed but keep the namespace Klacks.Api.Data.Seed,
    // which migrations reference and which therefore cannot be renamed.
    private const string SeedNamespaceRoot = "Data";
    private const string DataBaseContextEdge = "DataBaseContext";
    private const string EntityFrameworkEdge = "EntityFrameworkCore";

    private const string ApiNamespacePrefix = "Klacks.Api.";
    private const string EntityFrameworkNamespace = "Microsoft.EntityFrameworkCore";
    private const string GlobalUsingsFile = "GlobalUsings.cs";
    private const string CompositionRootSuffix = "ServiceCollectionExtensions.cs";
    private const string CompositionRootDirectory = @"Infrastructure\Extensions\";
    private const string EdgeSeparator = " -> ";

    private const string ShrinkOnlyHint =
        "KnownViolations may only LOSE entries. Do not add a line: move the type, introduce an interface " +
        "in the inner layer, or route the call through a repository instead. If that is genuinely " +
        "impossible, the owner decides - not the test.";

    // The project's own mediator abstractions (IRequest, IRequestHandler, IMediator) replace MediatR and
    // have no infrastructure dependency; backend-architecture.md "Imports" prescribes them for every handler.
    private const string SanctionedMediatorNamespace = "Klacks.Api.Infrastructure.Mediator";

    // The KnowledgeIndex module's public contract: its interfaces, constants and the domain vocabulary used
    // in their signatures. Application, Infrastructure and Presentation may consume it like an inner layer.
    private static readonly string[] SanctionedKnowledgeIndexContract =
    [
        "Klacks.Api.KnowledgeIndex.Application.Interfaces",
        "Klacks.Api.KnowledgeIndex.Application.Constants",
        "Klacks.Api.KnowledgeIndex.Domain",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> AllowedLayerTargets = new Dictionary<string, string[]>
    {
        [Domain] = [Domain],
        [Application] = [Domain, Application],
        [Infrastructure] = [Domain, Application, Infrastructure],
        [Presentation] = [Domain, Application, Presentation],
    };

    private static readonly IReadOnlyDictionary<string, int> MinimumScannedFilesPerLayer = new Dictionary<string, int>
    {
        [Domain] = 1000,
        [Application] = 1000,
        [Infrastructure] = 500,
        [Presentation] = 100,
    };

    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}"
    ];

    // "relative path -> referenced name". See ShrinkOnlyHint.
    private static readonly IReadOnlySet<string> KnownViolations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"Domain\Services\Assistant\RecipeEngineService.cs -> Klacks.Api.KnowledgeIndex.Application.Interfaces",
        @"Domain\Services\Assistant\RecipeEngineService.cs -> Klacks.Api.KnowledgeIndex.Domain",
        @"Domain\Services\Absences\AbsencePaginationService.cs -> Microsoft.EntityFrameworkCore",
        @"Domain\Services\ContainerTemplates\ContainerAvailableTasksService.cs -> Microsoft.EntityFrameworkCore",
        @"Domain\Services\Groups\GroupSearchService.cs -> Microsoft.EntityFrameworkCore",
        @"Domain\Services\Settings\CalendarRulePaginationService.cs -> Microsoft.EntityFrameworkCore",
        @"Domain\Services\Shifts\ShiftPaginationService.cs -> Microsoft.EntityFrameworkCore",
        @"Application\Services\Assistant\Evaluation\TurnEval\TurnEvalMemoryProbe.cs -> Klacks.Api.KnowledgeIndex.Application.Services",
        @"Application\Services\Assistant\Learning\SkillIndexStateVerifier.cs -> Klacks.Api.KnowledgeIndex.Application.Services",
        @"Presentation\Mcp\McpEndpointExtensions.cs -> Klacks.Api.Infrastructure.Authentication",
    };

    private static readonly Regex QualifiedApiName = new(
        @"\bKlacks\.Api\.(?:Domain|Application|Infrastructure|Presentation|KnowledgeIndex|Data)(?:\.[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex RelativeApiName = new(
        @"(?<![A-Za-z0-9_.])(?:Application|Infrastructure|Presentation|KnowledgeIndex|Data)(?:\.[A-Za-z_][A-Za-z0-9_]*)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex DataBaseContextName = new(
        @"\bDataBaseContext\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex EntityFrameworkName = new(
        @"\bMicrosoft\.EntityFrameworkCore\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Test]
    public void LayerDependencies_FollowTheLayerTable()
    {
        var scan = Scan();
        var unexpected = scan.Violations
            .Where(violation => !KnownViolations.Contains(violation.Key))
            .OrderBy(violation => violation.Key, StringComparer.Ordinal)
            .Select(violation => $"{violation.Key}{Environment.NewLine}    {string.Join($"{Environment.NewLine}    ", violation.Value)}")
            .ToList();

        unexpected.ShouldBeEmpty(
            $"Layer dependency violations:{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}" +
            $"{Environment.NewLine}{ShrinkOnlyHint}");
    }

    [Test]
    public void KnownViolations_ListOnlyFilesThatStillViolate()
    {
        var scan = Scan();
        var stale = KnownViolations
            .Where(entry => !scan.Violations.ContainsKey(entry))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            $"These KnownViolations entries no longer violate - delete them:{Environment.NewLine}" +
            $"{string.Join(Environment.NewLine, stale)}{Environment.NewLine}{ShrinkOnlyHint}");
    }

    [Test]
    public void Scan_CoversEveryLayer()
    {
        var scan = Scan();

        foreach (var (layer, minimum) in MinimumScannedFilesPerLayer)
        {
            scan.FilesPerLayer.GetValueOrDefault(layer).ShouldBeGreaterThan(
                minimum, $"The {layer} scan found too few files; the guard would pass vacuously.");
        }
    }

    [Test]
    public void Scanner_IgnoresCommentsAndStrings_ButSeesRelativeAndQualifiedNames()
    {
        const string source =
            "using Klacks.Api.Infrastructure.Persistence;\n" +
            "/// <see cref=\"Klacks.Api.Presentation.Controllers.Foo\"/>\n" +
            "var a = \"Klacks.Api.Presentation.X\"; // Klacks.Api.Presentation.Y\n" +
            "var b = @\"Presentation.Verbatim\n Presentation.SecondLine\";\n" +
            "var c = \"\"\"\n Presentation.Raw\n \"\"\";\n" +
            "var d = '\"'; var e = Application.Constants.Settings.Key;\n" +
            "/* Presentation.Block */ var f = DataBaseContext.Name;\n" +
            "var g = Klacks.Api.Data.Seed.ShiftSeed.Run;\n";

        var findings = FindReferences(Domain, CSharpSourceBlanker.BlankCommentsAndStrings(source))
            .Select(finding => $"{finding.Line}:{finding.Edge}")
            .ToList();

        findings.ShouldBe(["1:Infrastructure", "9:Application", "10:DataBaseContext", "11:Infrastructure"]);
    }

    private static ScanResult Scan()
    {
        var root = RepositoryRootLocator.ApiProject;
        var violations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var filesPerLayer = new Dictionary<string, int>();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (ExcludedPathFragments.Any(path.Contains))
            {
                continue;
            }

            var relative = NormalizeSeparators(Path.GetRelativePath(root, path));
            var layer = LayerOf(relative);
            if (layer == null)
            {
                continue;
            }

            filesPerLayer[layer] = filesPerLayer.GetValueOrDefault(layer) + 1;

            foreach (var finding in FindReferences(layer, CSharpSourceBlanker.BlankCommentsAndStrings(File.ReadAllText(path))))
            {
                var key = $"{relative}{EdgeSeparator}{finding.Reference}";
                if (!violations.TryGetValue(key, out var lines))
                {
                    lines = [];
                    violations[key] = lines;
                }

                lines.Add($"line {finding.Line}: {layer} -> {finding.Edge}");
            }
        }

        return new ScanResult(violations, filesPerLayer);
    }

    private static string? LayerOf(string relativePath)
    {
        if (relativePath.Equals(GlobalUsingsFile, StringComparison.OrdinalIgnoreCase))
        {
            return Domain;
        }

        if (relativePath.StartsWith(CompositionRootDirectory, StringComparison.OrdinalIgnoreCase)
            && relativePath.EndsWith(CompositionRootSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var separator = relativePath.IndexOf('\\');
        if (separator < 0)
        {
            return null;
        }

        var topFolder = relativePath[..separator];
        return AllowedLayerTargets.ContainsKey(topFolder) ? topFolder : null;
    }

    private static IEnumerable<Finding> FindReferences(string layer, string code)
    {
        var lines = code.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;

            var names = QualifiedApiName.Matches(line).Select(match => match.Value)
                .Concat(RelativeApiName.Matches(line).Select(match => ApiNamespacePrefix + match.Value));

            foreach (var name in names)
            {
                var edge = ForbiddenEdge(layer, name);
                if (edge != null)
                {
                    yield return new Finding(lineNumber, edge, name);
                }
            }

            if (layer != Infrastructure && DataBaseContextName.IsMatch(line))
            {
                yield return new Finding(lineNumber, DataBaseContextEdge, DataBaseContextEdge);
            }

            if (layer == Domain && EntityFrameworkName.IsMatch(line))
            {
                yield return new Finding(lineNumber, EntityFrameworkEdge, EntityFrameworkNamespace);
            }
        }
    }

    private static string? ForbiddenEdge(string layer, string qualifiedName)
    {
        var target = qualifiedName[ApiNamespacePrefix.Length..].Split('.')[0];
        if (target == SeedNamespaceRoot)
        {
            target = Infrastructure;
        }

        if (target == KnowledgeIndex)
        {
            return layer == Domain || !SanctionedKnowledgeIndexContract.Any(prefix => IsWithin(qualifiedName, prefix))
                ? KnowledgeIndex
                : null;
        }

        if (AllowedLayerTargets[layer].Contains(target))
        {
            return null;
        }

        if (target == Infrastructure && layer != Domain && IsWithin(qualifiedName, SanctionedMediatorNamespace))
        {
            return null;
        }

        return target;
    }

    private static bool IsWithin(string qualifiedName, string namespacePrefix)
        => qualifiedName.StartsWith(namespacePrefix, StringComparison.Ordinal)
           && (qualifiedName.Length == namespacePrefix.Length || qualifiedName[namespacePrefix.Length] == '.');

    // Path.GetRelativePath emits forward slashes on the ubuntu CI runner; the keys are written Windows-style.
    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    private sealed record Finding(int Line, string Edge, string Reference);

    private sealed record ScanResult(
        IReadOnlyDictionary<string, List<string>> Violations,
        IReadOnlyDictionary<string, int> FilesPerLayer);
}
