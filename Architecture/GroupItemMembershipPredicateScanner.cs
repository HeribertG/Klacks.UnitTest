// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Roslyn syntax scan for GroupItemMembershipPredicateGuardTests: finds, in one C# source text, the client-membership
/// queries on the group items that test the scenario token by hand (row.AnalyseToken == null, row.AnalyseToken is null,
/// !row.AnalyseToken.HasValue) instead of naming one of the predicates of RegularMembership. Since a borrowed person
/// holds temporary memberships of single days in the real plan, "no token" no longer says which memberships a reader
/// means: the regular ones (IsRegular), every one including the borrowed days (OrTemporary) or those of a period
/// (OrTemporaryWithin). A query is the whole expression around context.GroupItem or Set&lt;GroupItem&gt;(), found by
/// GroupItemShiftTokenScanner.FindGroupItemQueries. Its group item rows are the lambda parameters of the calls chained
/// on the set up to and including the first projection (Select, SelectMany, GroupBy, Join, GroupJoin), or the from or
/// join variable that ranges over the set. A row is a client membership when it also reads ClientId or Client inside
/// the query; a query that only follows shift links is left alone.
/// Known gaps, because this is syntax and not data flow: a query that reaches the group items through a navigation of
/// another root (client.GroupItems, shift.GroupItems) or through a parameter, and code that filters loaded rows in
/// memory, are not seen - the scan cannot tell a client's memberships from a shift's links there. A membership query
/// that never reads ClientId on its row, and a row behind a projection, are not seen either.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Klacks.UnitTest.Architecture;

public static class GroupItemMembershipPredicateScanner
{
    private const string TokenMemberName = "AnalyseToken";
    private const string HasValueName = "HasValue";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly string[] ClientMemberNames = ["ClientId", "Client"];

    private static readonly string[] ProjectingMethodNames = ["Select", "SelectMany", "GroupBy", "Join", "GroupJoin"];

    /// <param name="source">Full text of one C# file</param>
    /// <returns>The 1-based line of each hand-written token test of a client-membership query, in source order</returns>
    public static IReadOnlyList<int> FindHandWrittenTokenTestLines(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetRoot();
        var tests = new HashSet<SyntaxNode>();

        foreach (var (set, query) in GroupItemShiftTokenScanner.FindGroupItemQueries(root))
        {
            var rows = GroupItemRows(set);
            var clientRows = query.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(member => ClientMemberNames.Contains(member.Name.Identifier.Text))
                .Select(member => member.Expression is IdentifierNameSyntax row ? row.Identifier.Text : string.Empty)
                .Where(rows.Contains)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var node in query.DescendantNodes())
            {
                if (TokenNullTestRow(node) is { } testedRow && clientRows.Contains(testedRow))
                {
                    tests.Add(node);
                }
            }
        }

        return tests
            .Select(test => test.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .OrderBy(line => line)
            .ToList();
    }

    private static HashSet<string> GroupItemRows(ExpressionSyntax set)
    {
        var rows = new HashSet<string>(StringComparer.Ordinal);
        SyntaxNode current = set;

        while (true)
        {
            switch (current.Parent)
            {
                case MemberAccessExpressionSyntax member when member.Expression == current:
                    current = member;
                    continue;
                case InvocationExpressionSyntax invocation when invocation.Expression == current:
                    rows.UnionWith(LambdaParameterNames(invocation));
                    if (IsProjection(invocation))
                    {
                        return rows;
                    }

                    current = invocation;
                    continue;
                case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax wrapper } }:
                    current = wrapper;
                    continue;
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized;
                    continue;
                case FromClauseSyntax from:
                    rows.Add(from.Identifier.Text);
                    return rows;
                case JoinClauseSyntax join when join.InExpression == current:
                    rows.Add(join.Identifier.Text);
                    return rows;
                default:
                    return rows;
            }
        }
    }

    private static IEnumerable<string> LambdaParameterNames(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.SelectMany(argument => argument.Expression switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.Text],
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(p => p.Identifier.Text),
            _ => Enumerable.Empty<string>()
        });

    private static bool IsProjection(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax member
            && ProjectingMethodNames.Contains(member.Name.Identifier.Text);

    private static string? TokenNullTestRow(SyntaxNode node) => node switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) && IsNull(binary.Right) => TokenRow(binary.Left),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) && IsNull(binary.Left) => TokenRow(binary.Right),
        IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax constant } pattern when IsNull(constant.Expression) => TokenRow(pattern.Expression),
        PrefixUnaryExpressionSyntax negation when negation.IsKind(SyntaxKind.LogicalNotExpression)
            && negation.Operand is MemberAccessExpressionSyntax { Name.Identifier.Text: HasValueName } hasValue => TokenRow(hasValue.Expression),
        _ => null
    };

    private static bool IsNull(ExpressionSyntax expression) => expression.IsKind(SyntaxKind.NullLiteralExpression);

    private static string? TokenRow(ExpressionSyntax expression)
        => expression is MemberAccessExpressionSyntax { Name.Identifier.Text: TokenMemberName, Expression: IdentifierNameSyntax row }
            ? row.Identifier.Text
            : null;
}
