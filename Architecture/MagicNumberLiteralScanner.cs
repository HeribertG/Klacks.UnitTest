// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Roslyn syntax scan that finds the magic-number literals of one C# source text, for MagicNumberGuardTests.
/// A hit is a numeric literal expression whose value is neither 0 nor 1 (in any numeric type; -1 is a unary
/// minus over 1 and therefore exempt as well) and that is not part of a named-value definition:
/// - the initializer of a const field, a static readonly field or a const local (the literal IS the name),
/// - an attribute argument (attributes only take compile-time constants and are declarative metadata),
/// - an enum member value (the member name is the name of the number).
/// Comments, string contents and preprocessor directives are not literal expressions and never count;
/// inactive #if regions are not parsed.
/// </summary>

using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Klacks.UnitTest.Architecture;

public static class MagicNumberLiteralScanner
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <param name="source">Full text of one C# file</param>
    /// <returns>The 1-based line of every magic-number literal, in source order (a line may repeat)</returns>
    public static IReadOnlyList<int> FindMagicNumberLines(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetRoot();

        return root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.NumericLiteralExpression))
            .Where(literal => !IsTrivialValue(literal.Token.Value))
            .Where(literal => !IsInsideNamedValueDefinition(literal))
            .Select(literal => literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();
    }

    private static bool IsTrivialValue(object? value)
    {
        if (value is null)
        {
            return false;
        }

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return number == 0d || number == 1d;
    }

    private static bool IsInsideNamedValueDefinition(SyntaxNode literal)
    {
        foreach (var ancestor in literal.Ancestors())
        {
            switch (ancestor)
            {
                case AttributeSyntax:
                case EnumMemberDeclarationSyntax:
                    return true;
                case FieldDeclarationSyntax field:
                    return IsConstOrStaticReadonly(field.Modifiers);
                case LocalDeclarationStatementSyntax local:
                    return local.IsConst;
                case MemberDeclarationSyntax:
                    return false;
            }
        }

        return false;
    }

    private static bool IsConstOrStaticReadonly(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.ConstKeyword))
        {
            return true;
        }

        return modifiers.Any(SyntaxKind.StaticKeyword) && modifiers.Any(SyntaxKind.ReadOnlyKeyword);
    }
}
