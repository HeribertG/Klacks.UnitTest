// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Roslyn syntax scan for GroupItemShiftTokenGuardTests: finds the queries of one C# source text that start at the group
/// items, reach a shift (gi.Shift, gi.ShiftId) and never look at AnalyseToken. Such a query reads key shifts and scenario
/// clones next to real shifts; a key shift is a tokened row whose name is itself confidential.
/// A query is the whole expression around context.GroupItem or Set&lt;GroupItem&gt;(): the method chain, a wrapping call such
/// as a filter helper that takes the chain as an argument, and a query expression that names the set in a from or join
/// clause. It checks the token when any member access in it is named AnalyseToken or when it names one of
/// TokenFilterHelperNames (a filter helper, or the shared predicate of GroupItemRepository). A query that starts at
/// context.Group or Set&lt;Group&gt;() counts when it includes the group items without a filter on AnalyseToken or ShiftId.
/// Known gaps, because this is syntax and not data flow: a query kept in a variable and continued in a later statement is
/// seen only up to the variable, and a query that reaches the group items through a navigation of another root (s.GroupItems)
/// is no root; a shift or a client that includes its own links is not looked at.
/// </summary>

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Klacks.UnitTest.Architecture;

public static class GroupItemShiftTokenScanner
{
    private const string GroupItemName = "GroupItem";
    private const string GroupName = "Group";
    private const string GroupItemsName = "GroupItems";
    private const string IncludeMethodName = "Include";
    private const string ShiftIdName = "ShiftId";
    private const string SetMethodName = "Set";
    private const string ContextSuffix = "context";
    private const string TokenMemberName = "AnalyseToken";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly string[] ShiftMemberNames = ["Shift", "ShiftId"];

    private static readonly string[] TokenFilterHelperNames = ["WhereEitherWorld", "IsRealShiftLink"];

    /// <param name="source">Full text of one C# file</param>
    /// <returns>The 1-based line where each query without a token check starts, in source order</returns>
    public static IReadOnlyList<int> FindUntokenedQueryLines(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetRoot();
        var queries = new HashSet<SyntaxNode>();
        var lines = new List<int>();

        foreach (var set in root.DescendantNodes().OfType<ExpressionSyntax>().Where(expression => IsSetOf(expression, GroupItemName)))
        {
            var query = FindWholeQuery(set);
            if (queries.Add(query) && ReachesShift(query) && !ChecksToken(query))
            {
                lines.Add(set.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }

        foreach (var set in root.DescendantNodes().OfType<ExpressionSyntax>().Where(expression => IsSetOf(expression, GroupName)))
        {
            var query = FindWholeQuery(set);
            if (queries.Add(query) && IncludesGroupItemsWithoutFilter(query))
            {
                lines.Add(set.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }

        lines.Sort();
        return lines;
    }

    private static bool IsSetOf(ExpressionSyntax expression, string entityName) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text == entityName && IsContext(member.Expression),
        InvocationExpressionSyntax invocation => IsSetOfEntity(invocation.Expression, entityName),
        _ => false
    };

    private static bool IsContext(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text.EndsWith(ContextSuffix, StringComparison.OrdinalIgnoreCase),
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text.EndsWith(ContextSuffix, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsSetOfEntity(ExpressionSyntax target, string entityName)
    {
        var name = target is MemberAccessExpressionSyntax member ? member.Name : target as SimpleNameSyntax;

        return name is GenericNameSyntax { Identifier.Text: SetMethodName } generic
            && generic.TypeArgumentList.Arguments.Any(argument => argument.ToString() == entityName);
    }

    private static bool IncludesGroupItemsWithoutFilter(SyntaxNode query)
    {
        return query.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: IncludeMethodName })
            .Select(invocation => invocation.ArgumentList.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Select(identifier => identifier.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal))
            .Any(names => names.Contains(GroupItemsName) && !names.Contains(TokenMemberName) && !names.Contains(ShiftIdName));
    }

    private static SyntaxNode FindWholeQuery(SyntaxNode set)
    {
        var current = set;
        while (Enclosing(current) is { } enclosing)
        {
            current = enclosing;
        }

        return current;
    }

    private static SyntaxNode? Enclosing(SyntaxNode node) => node.Parent switch
    {
        MemberAccessExpressionSyntax member when member.Expression == node => member,
        InvocationExpressionSyntax invocation when invocation.Expression == node => invocation,
        PostfixUnaryExpressionSyntax postfix => postfix,
        ParenthesizedExpressionSyntax parenthesized => parenthesized,
        ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax wrapper } } => wrapper,
        FromClauseSyntax or JoinClauseSyntax => node.FirstAncestorOrSelf<QueryExpressionSyntax>(),
        _ => null
    };

    private static bool ReachesShift(SyntaxNode query)
    {
        var variables = RowVariables(query);

        return query.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Any(member => ShiftMemberNames.Contains(member.Name.Identifier.Text) && variables.Contains(RowOf(member.Expression)));
    }

    private static HashSet<string> RowVariables(SyntaxNode query)
    {
        var lambdaParameters = query.DescendantNodes().OfType<ParameterSyntax>().Select(parameter => parameter.Identifier.Text);
        var fromVariables = query.DescendantNodes().OfType<FromClauseSyntax>().Select(clause => clause.Identifier.Text);
        var joinVariables = query.DescendantNodes().OfType<JoinClauseSyntax>().Select(clause => clause.Identifier.Text);

        return lambdaParameters.Concat(fromVariables).Concat(joinVariables).ToHashSet(StringComparer.Ordinal);
    }

    private static string RowOf(ExpressionSyntax expression) => expression switch
    {
        PostfixUnaryExpressionSyntax postfix => RowOf(postfix.Operand),
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => string.Empty
    };

    private static bool ChecksToken(SyntaxNode query)
    {
        return query.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(identifier => identifier.Identifier.Text)
            .Any(name => name == TokenMemberName || TokenFilterHelperNames.Contains(name));
    }
}
