// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Roslyn syntax scan that finds the silent catch-all blocks of one C# source text, for SilentCatchAllGuardTests.
/// A catch-all is a catch clause without a type, or with the type Exception or System.Exception (with or without a
/// variable and with or without a when filter). It is silent when its block contains neither a throw (statement or
/// expression) nor an invocation of a logging method, searched recursively but without entering lambdas, anonymous
/// methods and local functions: code in those only runs if something invokes it later, so it does not prove that the
/// catch itself reports anything. A logging method is one whose simple name is Log or starts with Log followed by a
/// non-lowercase character (LogWarning, LogError, LogDebug, LogInformation, LogCritical, LogTrace, own helpers such
/// as LogUnreadable; Logout is not one). The scan is purely syntactic: the name Exception is not resolved to a type,
/// and a call such as Console.Error.WriteLine does not count as logging. The recursion counts every throw in the block,
/// so a conditional rethrow (if (x) throw;) makes the catch non-silent, and so does a log call inside a nested inner
/// catch; this is a deliberate loosening (no such case exists in Klacks.Api today).
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Klacks.UnitTest.Architecture;

public static class SilentCatchAllScanner
{
    private const string ExceptionTypeName = "Exception";
    private const string SystemNamespace = "System";
    private const string GlobalSystemNamespace = "global::System";
    private const string LogMethodPrefix = "Log";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <param name="source">Full text of one C# file</param>
    /// <returns>The 1-based line of every silent catch-all clause, in source order</returns>
    public static IReadOnlyList<int> FindSilentCatchAllLines(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetRoot();

        return root.DescendantNodes()
            .OfType<CatchClauseSyntax>()
            .Where(IsCatchAll)
            .Where(clause => !Reports(clause.Block))
            .Select(clause => clause.CatchKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();
    }

    private static bool IsCatchAll(CatchClauseSyntax clause)
    {
        var type = clause.Declaration?.Type;
        return type is null || IsExceptionType(type);
    }

    private static bool IsExceptionType(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == ExceptionTypeName,
        QualifiedNameSyntax qualified => IsQualifiedSystemException(qualified),
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText == ExceptionTypeName,
        _ => false
    };

    private static bool IsQualifiedSystemException(QualifiedNameSyntax qualified) =>
        qualified.Right.Identifier.ValueText == ExceptionTypeName &&
        qualified.Left.ToString() is SystemNamespace or GlobalSystemNamespace;

    private static bool Reports(SyntaxNode node)
    {
        foreach (var child in node.ChildNodes())
        {
            if (child is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                continue;
            }

            if (child is ThrowStatementSyntax or ThrowExpressionSyntax)
            {
                return true;
            }

            if (child is InvocationExpressionSyntax invocation && IsLoggingInvocation(invocation))
            {
                return true;
            }

            if (Reports(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoggingInvocation(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null
        };

        return name is not null && IsLogMethodName(name);
    }

    private static bool IsLogMethodName(string name)
    {
        if (!name.StartsWith(LogMethodPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return name.Length == LogMethodPrefix.Length || !char.IsLower(name[LogMethodPrefix.Length]);
    }
}
