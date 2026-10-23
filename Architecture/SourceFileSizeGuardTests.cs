// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Size guard for the Klacks.Api source tree: no file above FileLineLimit lines and no method body above
/// MethodLineLimit lines, unless it is named in one of the two allowlists WITH the line count it had when
/// it was added. The allowlists are ceilings, not exemptions - an allowlisted file that grows fails just
/// like a new offender, so the only way the list can change is downwards.
///
/// A source scan rather than reflection, for the same reason ForbidChallengeSchemeGuardTests scans: the
/// property being measured is a property of the text (lines, a method body), and reflection sees neither.
/// Type declarations are skipped because a file with a block-scoped namespace indents its class by four
/// spaces and is otherwise indistinguishable from a method signature.
///
/// Two properties of the allowlists are deliberate and easy to misread. First, a ceiling encodes what the
/// brace counter measured, not what a human would count: a brace inside a string literal can overstate a
/// body, and the number is still the right ceiling, because it is the number this test will produce
/// again. Second, the method key is "path::name", so overloads and same-named members of one file share
/// ONE ceiling, set by the longest of them - the guard exists to stop growth, not to tell members apart.
/// Introduced 2026-09-16 with the graceful-correction work, after LLMService.cs had reached 1760 lines
/// around a block that belonged in its own service.
/// </summary>

using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SourceFileSizeGuardTests
{
    private const int FileLineLimit = 800;
    private const int MethodLineLimit = 150;

    private const string ShrinkOnlyHint =
        "This allowlist may only LOSE entries. Do not add a line and do not raise a ceiling: split the " +
        "file or the method instead. If a split is genuinely impossible, the owner decides - not the test.";

    // .claude is excluded because Klacks.Api\.claude\worktrees\ holds gitignored symlinks to entirely
    // separate sibling repositories (e.g. Klacks.ScheduleOptimizer), kept there only for agent
    // convenience. Directory.EnumerateFiles follows them, so without this exclusion the guard would
    // police another repository's source under Klacks.Api's own ceilings - added 2026-09-16 after the
    // first run surfaced TokenEvolutionLoop.cs and TokenRepair.cs from that symlinked repo.
    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}Infrastructure{Path.DirectorySeparatorChar}Persistence{Path.DirectorySeparatorChar}Seed{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}"
    ];

    // Relative path -> its measured line count on the day it was allowlisted. See ShrinkOnlyHint.
    private static readonly IReadOnlyDictionary<string, int> FileCeilings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
    };

    // "RelativePath::MethodName" -> its measured body length on the day it was allowlisted.
    private static readonly IReadOnlyDictionary<string, int> MethodCeilings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
    };

    // Matches a member declaration at ANY indentation (file-scoped and block-scoped namespaces, nested types).
    // The return type is optional so constructors are measured, may contain spaces (generic arguments) and
    // parenthesised tuple types, and the name may carry generic type parameters. The first modifier must not be
    // "new", otherwise an object-creation line inside a field initializer would look like a member.
    private static readonly Regex MemberSignature = new(
        @"^\s*(?:\[[^\]]*\]\s*)?(?:public|private|protected|internal|static|async|override|virtual|sealed|partial|extern|unsafe)\s+" +
        @"(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|partial|new|extern|unsafe)\s+)*" +
        @"(?:(?:[\w<>\[\],\?\.]|\([^()]*\)|\s)+?\s+)?" +
        @"(\w+)\s*(?:<[^>()]*>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypeDeclaration = new(
        @"\b(class|record|struct|interface|enum)\b", RegexOptions.Compiled);

    /// <summary>
    /// Same walk-up as ForbidChallengeSchemeGuardTests.LocateApiProject, including its failure mode: a
    /// missing project THROWS. An Assert.Ignore here would turn "the repository is not checked out" into
    /// a silent pass, and a size guard that can silently pass is not a guard.
    /// </summary>
    private static string ApiRoot()
    {
        return RepositoryRootLocator.ApiProject;
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ExcludedPathFragments.Any(path.Contains))
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase));

    // The allowlist keys are written once, by a human, in the Windows-style form Path.GetRelativePath
    // produces locally. The backend-tests CI job (Klacks.Api/.github/workflows/tests.yml) runs on
    // ubuntu-latest, where GetRelativePath emits forward slashes - without this normalization every
    // allowlisted entry would silently stop matching on that runner and the guard would flag all of
    // them as new offenders on day one.
    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    [Test]
    public void NoSourceFile_ExceedsItsLineCeiling()
    {
        var root = ApiRoot();
        var offenders = new List<string>();

        foreach (var path in SourceFiles(root))
        {
            var relative = NormalizeSeparators(Path.GetRelativePath(root, path));
            var lines = File.ReadAllLines(path).Length;
            var ceiling = FileCeilings.TryGetValue(relative, out var allowed) ? allowed : FileLineLimit;

            if (lines > ceiling)
            {
                offenders.Add($"{relative}: {lines} lines (ceiling {ceiling})");
            }
        }

        offenders.ShouldBeEmpty(
            $"Files above their ceiling:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}" +
            $"{Environment.NewLine}{ShrinkOnlyHint}");
    }

    [Test]
    public void NoMethodBody_ExceedsItsLineCeiling()
    {
        var root = ApiRoot();
        var offenders = new List<string>();

        foreach (var path in SourceFiles(root))
        {
            var relative = NormalizeSeparators(Path.GetRelativePath(root, path));
            foreach (var (method, length) in MethodBodies(File.ReadAllLines(path)))
            {
                var key = $"{relative}::{method}";
                var ceiling = MethodCeilings.TryGetValue(key, out var allowed) ? allowed : MethodLineLimit;

                if (length > ceiling)
                {
                    offenders.Add($"{key}: {length} lines (ceiling {ceiling})");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"Method bodies above their ceiling:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}" +
            $"{Environment.NewLine}{ShrinkOnlyHint}");
    }

    private static bool IsBodylessTerminator(string line) =>
        line.TrimEnd().EndsWith(';') || line.Contains("=>", StringComparison.Ordinal);

    /// <summary>
    /// Brace-counting from a member signature to its matching close. Good enough on purpose: a brace
    /// inside a string literal can overstate a body, which errs towards flagging - and a flagged method
    /// is read by a human before anything is allowlisted. An expression-bodied member and an abstract
    /// declaration have no body and are skipped, including when the "=>" or the ";" sits on a later line of a
    /// multi-line signature.
    /// </summary>
    internal static IEnumerable<(string Method, int Length)> MethodBodies(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var match = MemberSignature.Match(lines[i]);
            if (!match.Success || TypeDeclaration.IsMatch(lines[i]))
            {
                continue;
            }

            if (lines[i].TrimEnd().EndsWith(';') || lines[i].Contains("=>", StringComparison.Ordinal))
            {
                continue;
            }

            var depth = 0;
            var started = false;
            var end = i;

            while (end < lines.Count)
            {
                if (!started && end > i && IsBodylessTerminator(lines[end]))
                {
                    break;
                }

                depth += lines[end].Count(c => c == '{') - lines[end].Count(c => c == '}');
                if (lines[end].Contains('{', StringComparison.Ordinal))
                {
                    started = true;
                }

                if (started && depth <= 0)
                {
                    break;
                }

                end++;
            }

            if (!started || end >= lines.Count)
            {
                continue;
            }

            yield return (match.Groups[1].Value, end - i + 1);
            i = end;
        }
    }

    [Test]
    public void MethodBodies_TupleReturnType_IsMeasured()
    {
        var source = Snippet(
            "    private async Task<(AnalyseScenarioResource Scenario, WizardApplyOutcome Outcome)> ApplyAsScenarioAsync(",
            3,
            "    ",
            "        int a,",
            "        int b)");

        Describe(source).ShouldBe("ApplyAsScenarioAsync:8");
    }

    [Test]
    public void MethodBodies_PlainTupleReturnType_IsMeasured()
    {
        var source = Snippet("    public static (int Low, int High) Range()", 2, "    ");

        Describe(source).ShouldBe("Range:5");
    }

    [Test]
    public void MethodBodies_GenericReturnTypeWithSpaces_IsMeasured()
    {
        var source = Snippet("    public Task<Dictionary<string, int>> Load()", 2, "    ");

        Describe(source).ShouldBe("Load:5");
    }

    [Test]
    public void MethodBodies_GenericMethod_IsMeasured()
    {
        var source = Snippet("    public static T Convert<T>(object value)", 2, "    ");

        Describe(source).ShouldBe("Convert:5");
    }

    [Test]
    public void MethodBodies_BlockScopedNamespaceIndentation_IsMeasured()
    {
        var source = Snippet("        public void Run()", 2, "        ");

        Describe(source).ShouldBe("Run:5");
    }

    [Test]
    public void MethodBodies_TabIndentation_IsMeasured()
    {
        var source = Snippet("\tpublic void Run()", 2, "\t");

        Describe(source).ShouldBe("Run:5");
    }

    [Test]
    public void MethodBodies_Constructor_IsMeasured()
    {
        var source = Snippet("    public WizardApplyService(int dependency)", 2, "    ");

        Describe(source).ShouldBe("WizardApplyService:5");
    }

    [Test]
    public void MethodBodies_ConstructorWithBaseCall_IsMeasured()
    {
        var source = Snippet("    protected WizardApplyService(int dependency)", 2, "    ", "        : base(dependency)");

        Describe(source).ShouldBe("WizardApplyService:6");
    }

    [Test]
    public void MethodBodies_StaticConstructor_IsMeasured()
    {
        var source = Snippet("    static WizardApplyService()", 2, "    ");

        Describe(source).ShouldBe("WizardApplyService:5");
    }

    [Test]
    public void MethodBodies_ExpressionBodiedMember_OnLaterLine_DoesNotSwallowNextMember()
    {
        var source = new List<string>
        {
            "    public int Answer()",
            "        => 42;",
            ""
        };
        source.AddRange(Snippet("    public void Next()", 2, "    "));

        Describe(source).ShouldBe("Next:5");
    }

    [Test]
    public void MethodBodies_ObjectCreationInInitializer_IsNotAMember()
    {
        var source = new List<string>
        {
            "    private static readonly Item[] Items =",
            "    [",
            "        new Item(\"a\"),",
            "        new Item(\"b\")",
            "    ];",
            "    private readonly Dictionary<string, int> map = new(StringComparer.Ordinal);"
        };

        MethodBodies(source).ShouldBeEmpty();
    }

    [Test]
    public void MethodBodies_TypeDeclarationWithPrimaryConstructor_IsNotAMember()
    {
        var source = new List<string>
        {
            "    public sealed class Handler(int dependency)",
            "    {",
            "    }"
        };

        MethodBodies(source).ShouldBeEmpty();
    }

    [Test]
    public void MethodBodies_BodyAboveTheLimit_IsReportedAboveTheLimit()
    {
        var source = Snippet("    private async Task<(int First, int Second)> Long()", MethodLineLimit, "    ");

        var measured = MethodBodies(source).ToList();

        measured.Count.ShouldBe(1);
        measured[0].Length.ShouldBeGreaterThan(MethodLineLimit);
    }

    private static List<string> Snippet(string signature, int bodyLines, string indent, params string[] continuation)
    {
        var lines = new List<string> { signature };
        lines.AddRange(continuation);
        lines.Add($"{indent}{{");
        for (var i = 0; i < bodyLines; i++)
        {
            lines.Add($"{indent}    var value{i} = {i};");
        }

        lines.Add($"{indent}}}");
        return lines;
    }

    private static string Describe(IReadOnlyList<string> lines) =>
        string.Join(",", MethodBodies(lines).Select(m => $"{m.Method}:{m.Length}"));
}
