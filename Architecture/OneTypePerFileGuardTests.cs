// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the owner rule "every class/interface in its own file" (.claude/rules/code-policies.md, binding
/// since 2026-10-20) for the Klacks.Api source tree. Two properties are checked:
/// 1. A file declares at most one top-level class, record, struct or interface. Nested types are fine, and
///    enums and delegates are not named by the rule, so neither is counted. A partial type declared twice in
///    one file counts once; Foo and Foo&lt;T&gt; are two types.
/// 2. A file that declares exactly one such type is named after it: Name.cs, Name.Part.cs (e.g. one part of
///    a partial type), or Name{T}.cs for a generic type (the StyleCop SA1649 form, used where a non-generic and
///    a generic type share a name, e.g. Infrastructure/Mediator/IRequest{TResponse}.cs).
///
/// A source scan rather than reflection: reflection sees types, not the file a type was declared in.
/// Comments, string/char literals and preprocessor lines are blanked first, then a token walk tracks the
/// brace scopes - a declaration counts only while every enclosing scope is a namespace, which covers
/// file-scoped and block-scoped namespaces alike. "record" is a contextual keyword, so it counts only after
/// a modifier, an attribute or a statement/brace boundary; "class"/"struct" after ':' or ',' are generic
/// constraints and do not count.
///
/// Scope: every .cs file under Klacks.Api except bin/, obj/, Migrations/ (generated) and .claude/ (symlinks
/// to other repositories, see SourceFileSizeGuardTests). Infrastructure/Persistence/Seed IS scanned:
/// SourceFileSizeGuardTests excludes it because its data files are long, which says nothing about how many
/// types a file declares. The Klacks.Plugin.* and Klacks.Api.SourceGenerators projects are separate
/// repositories and not scanned here.
///
/// KnownMultiTypeFiles ("relative path -> declared type count") is shrink-only: a file above its count
/// fails, and so does an entry whose file now declares fewer types or no longer exists. It was emptied in
/// the change that introduced the guard (2026-10-21, 77 files split); keep it empty.
/// </summary>

using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class OneTypePerFileGuardTests
{
    private const int MaxTypesPerFile = 1;
    private const int MinimumScannedFiles = 5000;
    private const string SeedDirectory = @"Infrastructure\Persistence\Seed\";
    private const string NamespaceKeyword = "namespace";
    private const string RecordKeyword = "record";
    private const string ClassKeyword = "class";
    private const string StructKeyword = "struct";
    private const string WhereKeyword = "where";
    private const string SourceFilePattern = "*.cs";

    private const string ShrinkOnlyHint =
        "KnownMultiTypeFiles may only LOSE entries. Do not add a line and do not raise a count: move the " +
        "extra type into its own file named after it (same folder, same namespace). If that is genuinely " +
        "impossible, the owner decides - not the test.";

    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}"
    ];

    // Relative path -> number of top-level types the file declared when it was allowlisted. See ShrinkOnlyHint.
    private static readonly IReadOnlyDictionary<string, int> KnownMultiTypeFiles =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CountedKeywords = new(StringComparer.Ordinal)
    {
        ClassKeyword, RecordKeyword, StructKeyword, "interface",
    };

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "internal", "private", "protected", "sealed", "static", "abstract", "partial",
        "readonly", "ref", "unsafe", "new", "file",
    };

    private static readonly HashSet<string> ConstraintPrefixes = new(StringComparer.Ordinal) { ":", ",", "." };

    private static readonly HashSet<string> RecordBoundaries = new(StringComparer.Ordinal) { "]", ";", "{", "}" };

    private static readonly Regex Token = new(
        @"@?[A-Za-z_][A-Za-z0-9_]*|\S",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex PreprocessorLine = new(
        @"^[ \t]*#.*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));

    private static readonly Lazy<ScanResult> CachedScan = new(ScanApiTree);

    private static readonly Regex Identifier = new(
        @"^@?[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Test]
    public void EverySourceFile_DeclaresAtMostOneTopLevelType()
    {
        var offenders = Scan().Files
            .Where(file => file.Types.Count > KnownMultiTypeFiles.GetValueOrDefault(file.RelativePath, MaxTypesPerFile))
            .Select(file => $"{file.RelativePath}: {string.Join(", ", file.Types)}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"Files declaring more than one top-level class/record/struct/interface:{Environment.NewLine}" +
            $"{string.Join(Environment.NewLine, offenders)}{Environment.NewLine}{ShrinkOnlyHint}");
    }

    [Test]
    public void KnownMultiTypeFiles_ListOnlyFilesThatStillDeclareThatMany()
    {
        var counts = Scan().Files.ToDictionary(file => file.RelativePath, file => file.Types.Count, StringComparer.OrdinalIgnoreCase);
        var stale = KnownMultiTypeFiles
            .Where(entry => counts.GetValueOrDefault(entry.Key) < entry.Value)
            .Select(entry => $"{entry.Key}: listed {entry.Value}, declares {counts.GetValueOrDefault(entry.Key)}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            $"These KnownMultiTypeFiles entries are stale - lower or delete them:{Environment.NewLine}" +
            $"{string.Join(Environment.NewLine, stale)}{Environment.NewLine}{ShrinkOnlyHint}");
    }

    [Test]
    public void EverySingleTypeFile_IsNamedAfterItsType()
    {
        var offenders = Scan().Files
            .Where(file => file.Types.Count == 1 && !IsNamedAfter(file.FileName, file.Types[0]))
            .Select(file => $"{file.RelativePath}: declares {file.Types[0]}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"Files not named after the type they declare (expected Name.cs, Name.Part.cs or Name{{T}}.cs):" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Test]
    public void Scan_CoversTheApiTree_IncludingSeed()
    {
        var files = Scan().Files;

        files.Count.ShouldBeGreaterThan(MinimumScannedFiles, "The scan found too few files; the guard would pass vacuously.");
        files.ShouldContain(file => file.RelativePath.StartsWith(SeedDirectory, StringComparison.OrdinalIgnoreCase),
            "Infrastructure/Persistence/Seed must be scanned.");
    }

    [Test]
    public void Scanner_CountsTopLevelTypesOnly()
    {
        const string source =
            "using X;\n" +
            "namespace A.B;\n" +
            "/// <summary>class FakeInDoc</summary>\n" +
            "[Attr(typeof(string))]\n" +
            "public sealed partial class First<T> : Base where T : class, new()\n" +
            "{\n" +
            "    private sealed record Nested(int A);\n" +
            "    private const string S = \"class InString\";\n" +
            "    private const char C = '{';\n" +
            "    public void M() { foreach (var record in items) { } var d = delegate { }; }\n" +
            "    // interface InComment\n" +
            "    public struct NestedStruct { }\n" +
            "}\n" +
            "public partial class First<T> { }\n" +
            "internal record struct Second(int X);\n" +
            "public enum NotCounted { A }\n" +
            "public delegate void NotCountedEither<TArg>(TArg arg);\n" +
            "public interface IThird;\n" +
            "public record Fourth(int A) : IThird;\n" +
            "file class First { }\n" +
            "public class Fifth<TKey, TValue> where TKey : struct { }\n";

        var types = DeclaredTypes(Blank(source)).Select(type => type.ToString()).ToList();

        types.ShouldBe(["First<1>", "Second", "IThird", "Fourth", "First", "Fifth<2>"]);
    }

    [Test]
    public void Scanner_HandlesBlockScopedNamespaces()
    {
        const string source =
            "namespace A\n" +
            "{\n" +
            "    public class Outer\n" +
            "    {\n" +
            "        public class Inner { }\n" +
            "    }\n" +
            "    namespace C { internal interface IDeep { } }\n" +
            "}\n";

        var types = DeclaredTypes(Blank(source)).Select(type => type.ToString()).ToList();

        types.ShouldBe(["Outer", "IDeep"]);
    }

    [Test]
    public void Scanner_IgnoresContextualRecordInTopLevelStatements_AndSeesRecordsAfterDirectives()
    {
        const string source =
            "var record = 1;\n" +
            "record = 2;\n" +
            "System.Console.WriteLine(record);\n" +
            "#region Types\n" +
            "record Pair(int A, int B);\n" +
            "#endregion\n";

        var types = DeclaredTypes(Blank(source)).Select(type => type.ToString()).ToList();

        types.ShouldBe(["Pair"]);
    }

    [TestCase("Foo.cs", "Foo", 0, ExpectedResult = true)]
    [TestCase("Foo.Diagnostics.cs", "Foo", 0, ExpectedResult = true)]
    [TestCase("Foo.cs", "Foo", 1, ExpectedResult = true)]
    [TestCase("Foo{TResponse}.cs", "Foo", 1, ExpectedResult = true)]
    [TestCase("Foo{TResponse}.cs", "Foo", 0, ExpectedResult = false)]
    [TestCase("foo.cs", "Foo", 0, ExpectedResult = false)]
    [TestCase("FooQueries.cs", "Foo", 0, ExpectedResult = false)]
    public bool FileNameRule_AcceptsExactPartialAndGenericForms(string fileName, string typeName, int arity)
    {
        return IsNamedAfter(fileName, new DeclaredType(typeName, arity));
    }

    private static ScanResult Scan() => CachedScan.Value;

    private static string Blank(string source) =>
        PreprocessorLine.Replace(CSharpSourceBlanker.BlankCommentsAndStrings(source), string.Empty);

    private static ScanResult ScanApiTree()
    {
        var root = RepositoryRootLocator.ApiProject;
        var files = new List<ScannedFile>();

        foreach (var path in Directory.EnumerateFiles(root, SourceFilePattern, SearchOption.AllDirectories))
        {
            if (ExcludedPathFragments.Any(path.Contains))
            {
                continue;
            }

            var relative = NormalizeSeparators(Path.GetRelativePath(root, path));
            var types = DeclaredTypes(Blank(File.ReadAllText(path)));
            files.Add(new ScannedFile(relative, Path.GetFileName(path), types));
        }

        return new ScanResult(files);
    }

    /// <summary>
    /// Token walk over blanked source. A '{' opens a namespace scope when the last header seen was a namespace
    /// declaration, otherwise a non-namespace scope (type body, member body, initializer); a type declaration is
    /// top-level while every open scope is a namespace scope.
    /// </summary>
    private static IReadOnlyList<DeclaredType> DeclaredTypes(string code)
    {
        var tokens = Token.Matches(code).Select(match => match.Value).ToList();
        var scopes = new Stack<bool>();
        var namespacePending = false;
        var types = new List<DeclaredType>();

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var previous = index > 0 ? tokens[index - 1] : null;

            switch (token)
            {
                case NamespaceKeyword:
                    namespacePending = true;
                    continue;
                case "{":
                    scopes.Push(namespacePending);
                    namespacePending = false;
                    continue;
                case "}":
                    if (scopes.Count > 0)
                    {
                        scopes.Pop();
                    }

                    continue;
                case ";":
                    namespacePending = false;
                    continue;
            }

            if (!IsTypeDeclarationKeyword(token, previous))
            {
                continue;
            }

            var nameIndex = index + 1;
            if (token == RecordKeyword && nameIndex < tokens.Count && (tokens[nameIndex] == StructKeyword || tokens[nameIndex] == ClassKeyword))
            {
                nameIndex++;
            }

            if (nameIndex >= tokens.Count || !Identifier.IsMatch(tokens[nameIndex]) || tokens[nameIndex] == WhereKeyword)
            {
                continue;
            }

            if (!scopes.All(isNamespace => isNamespace))
            {
                continue;
            }

            var type = new DeclaredType(tokens[nameIndex].TrimStart('@'), Arity(tokens, nameIndex + 1));
            if (!types.Contains(type))
            {
                types.Add(type);
            }
        }

        return types;
    }

    private static bool IsTypeDeclarationKeyword(string token, string? previous)
    {
        if (!CountedKeywords.Contains(token))
        {
            return false;
        }

        if (previous != null && ConstraintPrefixes.Contains(previous))
        {
            return false;
        }

        if ((token == StructKeyword || token == ClassKeyword) && previous == RecordKeyword)
        {
            return false;
        }

        return token != RecordKeyword
               || previous == null
               || Modifiers.Contains(previous)
               || RecordBoundaries.Contains(previous);
    }

    private static int Arity(IReadOnlyList<string> tokens, int index)
    {
        if (index >= tokens.Count || tokens[index] != "<")
        {
            return 0;
        }

        var depth = 0;
        var arity = 1;
        for (; index < tokens.Count; index++)
        {
            switch (tokens[index])
            {
                case "<":
                    depth++;
                    break;
                case ">":
                    depth--;
                    if (depth == 0)
                    {
                        return arity;
                    }

                    break;
                case "," when depth == 1:
                    arity++;
                    break;
            }
        }

        return arity;
    }

    private static bool IsNamedAfter(string fileName, DeclaredType type)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (baseName.Equals(type.Name, StringComparison.Ordinal)
            || baseName.StartsWith(type.Name + ".", StringComparison.Ordinal))
        {
            return true;
        }

        return type.Arity > 0
               && baseName.StartsWith(type.Name + "{", StringComparison.Ordinal)
               && baseName.EndsWith('}');
    }

    // Path.GetRelativePath emits forward slashes on the ubuntu CI runner; the keys are written Windows-style.
    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    private sealed record DeclaredType(string Name, int Arity)
    {
        public override string ToString() => Arity == 0 ? Name : $"{Name}<{Arity}>";
    }

    private sealed record ScannedFile(string RelativePath, string FileName, IReadOnlyList<DeclaredType> Types);

    private sealed record ScanResult(IReadOnlyList<ScannedFile> Files);
}
