// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Size guard for the Klacks.Api source tree: no file above FileLineLimit lines and no member body above
/// MethodLineLimit lines, unless it is named in one of the two allowlists WITH the line count it had when
/// it was added. The allowlists are ceilings, not exemptions - an allowlisted file that grows fails just
/// like a new offender, so the only way the list can change is downwards.
///
/// A source scan rather than reflection, for the same reason ForbidChallengeSchemeGuardTests scans: the
/// property being measured is a property of the text (lines, a method body), and reflection sees neither.
/// Since 2026-10-23 the members are found by the Roslyn parser (Microsoft.CodeAnalysis.CSharp syntax trees, no
/// compilation), not by a regex and a brace counter. That closes the gaps the regex had: members without a
/// modifier, explicit interface implementations, operators, and braces inside strings, raw strings and comments.
///
/// What is measured: every BaseMethodDeclarationSyntax (methods, constructors, destructors, operators, conversion
/// operators) and every AccessorDeclarationSyntax (get/set/init/add/remove) that has a block body or an
/// expression body, plus expression-bodied properties and indexers ("=>" on the property itself). Bodyless
/// declarations (abstract, interface, partial, extern, auto-accessors) are not members with a body and are skipped.
/// Local functions are not measured on their own: inside a measured member, that member contains their lines and
/// therefore exceeds the limit first. Local functions of top-level statements (Program.cs) have no enclosing member
/// and are not measured at all (known gap; the regex guard measured them). Lambdas, field initializers and
/// top-level statements are not members; FileLineLimit covers them.
///
/// How a member is counted is unchanged from the regex guard: from the first line of the declaration (the
/// modifier or, without one, the return type or name - attribute lists and doc comments above it do not count)
/// to the line of its last token, both included. For a block body that is the closing brace, for an expression
/// body the semicolon. A file the parser rejects is reported as an offender, because a file that cannot be
/// measured must not pass silently.
///
/// The method key is "path::name", so overloads and same-named members of one file share ONE ceiling, set by
/// the longest of them - the guard exists to stop growth, not to tell members apart. Accessors are keyed
/// "Property.get", operators "operator +", conversions "implicit operator T", destructors "~Type".
/// Introduced 2026-09-16 with the graceful-correction work, after LLMService.cs had reached 1760 lines
/// around a block that belonged in its own service.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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

    private const string ProbeTypeName = "WizardApplyService";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

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
            var tree = Parse(File.ReadAllText(path));
            var parseError = FirstParseError(tree);
            if (parseError is not null)
            {
                offenders.Add($"{relative}: cannot be measured, the parser rejects it ({parseError})");
                continue;
            }

            foreach (var (method, line, length) in MethodBodies(tree))
            {
                var key = $"{relative}::{method}";
                var ceiling = MethodCeilings.TryGetValue(key, out var allowed) ? allowed : MethodLineLimit;

                if (length > ceiling)
                {
                    offenders.Add($"{key}: {length} lines from line {line} (ceiling {ceiling})");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"Method bodies above their ceiling:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}" +
            $"{Environment.NewLine}{ShrinkOnlyHint}");
    }

    internal static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(source, ParseOptions);

    internal static Diagnostic? FirstParseError(SyntaxTree tree) =>
        tree.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// Every measured member of the tree in document order, with its 1-based first line and its length in lines.
    /// </summary>
    /// <param name="tree">A syntax tree without parse errors; see FirstParseError.</param>
    internal static IEnumerable<(string Method, int Line, int Length)> MethodBodies(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes()
            .Select(node => (Node: node, Name: MeasuredName(node)))
            .Where(member => member.Name is not null)
            .Select(member => Measure(tree, member.Node, member.Name!));

    private static string? MeasuredName(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax { Body: null, ExpressionBody: null } => null,
        MethodDeclarationSyntax method => method.Identifier.Text,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
        DestructorDeclarationSyntax destructor => $"{destructor.TildeToken.Text}{destructor.Identifier.Text}",
        OperatorDeclarationSyntax op => $"{op.OperatorKeyword.Text} {op.OperatorToken.Text}",
        ConversionOperatorDeclarationSyntax conversion =>
            $"{conversion.ImplicitOrExplicitKeyword.Text} {conversion.OperatorKeyword.Text} {conversion.Type}",
        AccessorDeclarationSyntax { Body: null, ExpressionBody: null } => null,
        AccessorDeclarationSyntax accessor => $"{AccessorOwnerName(accessor)}.{accessor.Keyword.Text}",
        PropertyDeclarationSyntax { ExpressionBody: not null } property => property.Identifier.Text,
        IndexerDeclarationSyntax { ExpressionBody: not null } indexer => indexer.ThisKeyword.Text,
        _ => null
    };

    private static string AccessorOwnerName(AccessorDeclarationSyntax accessor) => accessor.Parent?.Parent switch
    {
        PropertyDeclarationSyntax property => property.Identifier.Text,
        IndexerDeclarationSyntax indexer => indexer.ThisKeyword.Text,
        EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.Text,
        _ => string.Empty
    };

    private static (string Method, int Line, int Length) Measure(SyntaxTree tree, SyntaxNode member, string name)
    {
        var first = FirstTokenAfterAttributes(member);
        var span = tree.GetLineSpan(TextSpan.FromBounds(first.SpanStart, member.GetLastToken().Span.End));
        var firstLine = span.StartLinePosition.Line;

        return (name, firstLine + 1, span.EndLinePosition.Line - firstLine + 1);
    }

    private static SyntaxToken FirstTokenAfterAttributes(SyntaxNode member)
    {
        var first = member.ChildNodesAndTokens().First(child => !child.IsKind(SyntaxKind.AttributeList));

        return first.IsToken ? first.AsToken() : first.AsNode()!.GetFirstToken();
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
    public void MethodBodies_ModifierlessTupleReturnType_IsMeasured()
    {
        var source = Snippet("    (int Low, int High) Range()", 2, "    ");

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
        var source = Snippet($"    public {ProbeTypeName}(int dependency)", 2, "    ");

        Describe(source).ShouldBe($"{ProbeTypeName}:5");
    }

    [Test]
    public void MethodBodies_ConstructorWithBaseCall_IsMeasured()
    {
        var source = Snippet($"    protected {ProbeTypeName}(int dependency)", 2, "    ", "        : base(dependency)");

        Describe(source).ShouldBe($"{ProbeTypeName}:6");
    }

    [Test]
    public void MethodBodies_ModifierlessConstructorWithThisCall_IsMeasured()
    {
        var source = Snippet($"    {ProbeTypeName}()", 2, "    ", "        : this(0)");

        Describe(source).ShouldBe($"{ProbeTypeName}:6");
    }

    [Test]
    public void MethodBodies_StaticConstructor_IsMeasured()
    {
        var source = Snippet($"    static {ProbeTypeName}()", 2, "    ");

        Describe(source).ShouldBe($"{ProbeTypeName}:5");
    }

    [Test]
    public void MethodBodies_Destructor_IsMeasured()
    {
        var source = Snippet($"    ~{ProbeTypeName}()", 2, "    ");

        Describe(source).ShouldBe($"~{ProbeTypeName}:5");
    }

    [Test]
    public void MethodBodies_Operator_IsMeasured()
    {
        var source = Snippet($"    public static {ProbeTypeName} operator +({ProbeTypeName} left, {ProbeTypeName} right)", 2, "    ");

        Describe(source).ShouldBe("operator +:5");
    }

    [Test]
    public void MethodBodies_ConversionOperator_IsMeasured()
    {
        var source = Snippet($"    public static implicit operator int({ProbeTypeName} value)", 2, "    ");

        Describe(source).ShouldBe("implicit operator int:5");
    }

    [Test]
    public void MethodBodies_ExpressionBodiedMember_OnLaterLine_IsMeasuredToItsSemicolon_AndDoesNotSwallowNextMember()
    {
        var source = new List<string>
        {
            "    public int Answer()",
            "        => 42;",
            ""
        };
        source.AddRange(Snippet("    public void Next()", 2, "    "));

        Describe(source).ShouldBe("Answer:2,Next:5");
    }

    [Test]
    public void MethodBodies_SingleLineExpressionBody_IsMeasured()
    {
        var source = new List<string> { "    private static int Answer() => 42;" };

        Describe(source).ShouldBe("Answer:1");
    }

    [Test]
    public void MethodBodies_MultiLineExpressionBody_IsMeasuredToItsSemicolon()
    {
        var source = new List<string>
        {
            "    private static string Join(IEnumerable<string> values) =>",
            "        string.Join(\", \",",
            "            values);"
        };

        Describe(source).ShouldBe("Join:3");
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
            "    ];"
        };

        Describe(source).ShouldBeEmpty();
    }

    [Test]
    public void MethodBodies_GenericConstraintClass_IsMeasured()
    {
        var source = Snippet("    public static T Create<T>(object value) where T : class", 2, "    ");

        Describe(source).ShouldBe("Create:5");
    }

    [Test]
    public void MethodBodies_GenericConstraintStruct_IsMeasured()
    {
        var source = Snippet("    private static T? Read<T>(string raw) where T : struct", 2, "    ");

        Describe(source).ShouldBe("Read:5");
    }

    [Test]
    public void MethodBodies_GenericConstraintOnItsOwnLine_IsMeasured()
    {
        var source = Snippet("    T Create<T>()", 2, "    ", "        where T : class, new()");

        Describe(source).ShouldBe("Create:6");
    }

    [Test]
    public void MethodBodies_ParameterNamedRecord_IsMeasured()
    {
        var source = Snippet("    private async Task Save(Entity record, CancellationToken ct)", 2, "    ");

        Describe(source).ShouldBe("Save:5");
    }

    [Test]
    public void MethodBodies_ModifierlessMemberWithParameterNamedRecord_IsMeasured()
    {
        var source = Snippet("    Task Save(Entity record, Type @class)", 2, "    ");

        Describe(source).ShouldBe("Save:5");
    }

    [Test]
    public void MethodBodies_MemberWithoutModifier_IsMeasured()
    {
        var source = Snippet("    void Implicit()", 2, "    ");

        Describe(source).ShouldBe("Implicit:5");
    }

    [Test]
    public void MethodBodies_ExplicitInterfaceImplementation_IsMeasured()
    {
        var source = Snippet("    void IDisposable.Dispose()", 2, "    ");

        Describe(source).ShouldBe("Dispose:5");
    }

    [Test]
    public void MethodBodies_AttributeAndDocCommentAboveTheSignature_AreNotCounted()
    {
        var source = new List<string>
        {
            "    /// <summary>",
            "    /// Runs.",
            "    /// </summary>",
            "    [Obsolete]"
        };
        source.AddRange(Snippet("    public void Run()", 2, "    "));

        Describe(source).ShouldBe("Run:5");
    }

    [Test]
    public void MethodBodies_ClosingBraceInStringLiteral_IsNotCounted()
    {
        var source = new List<string>
        {
            "    public string Run()",
            "    {",
            "        var close = \"}\";",
            "        var verbatim = @\"}}\";",
            "        var interpolated = $\"{{{close}\";",
            "        return close;",
            "    }"
        };

        Describe(source).ShouldBe("Run:7");
    }

    [Test]
    public void MethodBodies_OpeningBraceInRawStringLiteral_DoesNotSwallowNextMember()
    {
        var source = new List<string>
        {
            "    public string Template()",
            "    {",
            "        return \"\"\"",
            "            { \"open\": [",
            "            \"\"\";",
            "    }",
            ""
        };
        source.AddRange(Snippet("    public void Next()", 2, "    "));

        Describe(source).ShouldBe("Template:6,Next:5");
    }

    [Test]
    public void MethodBodies_ClosingBraceInComment_IsNotCounted()
    {
        var source = new List<string>
        {
            "    public void Run()",
            "    {",
            "        // }",
            "        /* } */",
            "        var value = 1;",
            "    }"
        };

        Describe(source).ShouldBe("Run:6");
    }

    [Test]
    public void MethodBodies_BlockAccessors_AreMeasuredOneByOne()
    {
        var source = new List<string>
        {
            "    private int _value;",
            "    public int Value",
            "    {",
            "        get",
            "        {",
            "            return _value;",
            "        }",
            "        private set",
            "        {",
            "            _value = value;",
            "        }",
            "    }"
        };

        Describe(source).ShouldBe("Value.get:4,Value.set:4");
    }

    [Test]
    public void MethodBodies_ExpressionBodiedAccessorAndProperty_AreMeasured_AutoAccessorsAreNot()
    {
        var source = new List<string>
        {
            "    public int Auto { get; init; }",
            "    public int Value",
            "    {",
            "        get => _value;",
            "        set => _value = value;",
            "    }",
            "    public int Total =>",
            "        Auto + Value;"
        };

        Describe(source).ShouldBe("Value.get:1,Value.set:1,Total:2");
    }

    [Test]
    public void MethodBodies_IndexerAndEventAccessors_AreMeasured()
    {
        var source = new List<string>
        {
            "    public int this[int index]",
            "    {",
            "        get { return index; }",
            "    }",
            "    public event EventHandler Changed",
            "    {",
            "        add { }",
            "        remove { }",
            "    }"
        };

        Describe(source).ShouldBe("this.get:1,Changed.add:1,Changed.remove:1");
    }

    [Test]
    public void MethodBodies_BodylessDeclarations_AreNotMeasured()
    {
        var source = new List<string>
        {
            "    public abstract Task Run(",
            "        int a);",
            "    partial void Hook();",
            "    [System.Runtime.InteropServices.DllImport(\"native\")]",
            "    private static extern int Native();",
            "    private interface IProbe",
            "    {",
            "        void Contract();",
            "    }"
        };

        Describe(source).ShouldBeEmpty();
    }

    [Test]
    public void MethodBodies_DefaultInterfaceMethod_IsMeasured()
    {
        var source = new List<string> { "    private interface IProbe", "    {" };
        source.AddRange(Snippet("        void Default()", 2, "        "));
        source.Add("    }");

        Describe(source).ShouldBe("Default:5");
    }

    [Test]
    public void MethodBodies_LocalFunction_CountsOnlyAsPartOfItsMember()
    {
        var source = new List<string>
        {
            "    public int Run()",
            "    {",
            "        return Local();",
            "",
            "        int Local()",
            "        {",
            "            return 1;",
            "        }",
            "    }"
        };

        Describe(source).ShouldBe("Run:9");
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

        Describe(source).ShouldBeEmpty();
    }

    [Test]
    public void MethodBodies_MembersOfNestedTypes_AreMeasured()
    {
        var source = new List<string> { "    private sealed class Nested", "    {" };
        source.AddRange(Snippet("        public void Inner()", 2, "        "));
        source.Add("    }");

        Describe(source).ShouldBe("Inner:5");
    }

    [Test]
    public void FirstParseError_SourceTheParserRejects_IsReported()
    {
        var tree = Parse($"public class {ProbeTypeName} {{ public void Run( {{ }}");

        FirstParseError(tree).ShouldNotBeNull();
    }

    [Test]
    public void MethodBodies_BodyAboveTheLimit_IsReportedAboveTheLimit()
    {
        var source = Snippet("    private async Task<(int First, int Second)> Long()", MethodLineLimit, "    ");

        var measured = MethodBodies(ParseProbe(source)).ToList();

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

    private static SyntaxTree ParseProbe(IReadOnlyList<string> members)
    {
        var lines = new List<string> { $"public abstract partial class {ProbeTypeName}", "{" };
        lines.AddRange(members);
        lines.Add("}");

        var tree = Parse(string.Join("\n", lines));
        FirstParseError(tree).ShouldBeNull();
        return tree;
    }

    private static string Describe(IReadOnlyList<string> members) =>
        string.Join(",", MethodBodies(ParseProbe(members)).Select(m => $"{m.Method}:{m.Length}"));
}
