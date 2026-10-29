// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the shift list view routes: the order review skills put ShiftListViewRoutes URLs into their result
/// Data (start_order_review: view=orders, execute_order_review: view=plannable with from/until), and the Angular shift
/// list parses them with the constants in Klacks.Ui/src/app/domain/constants/shift-list-view.constants.ts. A route,
/// query parameter name or view value that differs on one side opens the list without the requested view, silently.
///
/// Like PermissionConstantsUiMirrorGuardTests this is a source scan of the TypeScript file. Klacks.Ui is located next
/// to Klacks.Api; when it is not checked out there the guard reports itself inconclusive. A Klacks.Ui that is checked
/// out but lacks the file, or a constant the scan cannot read, is a hard failure.
///
/// Scope note: only the string literals of SHIFT_LIST_ROUTE, SHIFT_LIST_VIEW_QUERY_PARAM, SHIFT_LIST_FROM_QUERY_PARAM,
/// SHIFT_LIST_UNTIL_QUERY_PARAM and the Orders/Plannable members of SHIFT_LIST_VIEW are compared; how the Ui applies a
/// view (filter, sort) is covered by its own specs.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ShiftListViewRoutesUiMirrorGuardTests
{
    private const string UiProjectDirectory = "Klacks.Ui";

    private static readonly string[] ConstantsFileSegments =
    [
        "src", "app", "domain", "constants", "shift-list-view.constants.ts"
    ];

    private static readonly Regex ViewObjectDeclaration = new(
        @"(?:export\s+)?const\s+SHIFT_LIST_VIEW\s*(?::[^=]+)?=\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled);

    [Test]
    public void UiShiftListViewConstants_MustMirrorShiftListViewRoutes()
    {
        var uiProject = RepositoryRootLocator.FindDirectory(UiProjectDirectory);
        if (uiProject == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiProjectDirectory));
            return;
        }

        var path = Path.Combine(uiProject, Path.Combine(ConstantsFileSegments));
        File.Exists(path).ShouldBeTrue(
            $"The Angular shift list view constants file is missing: {path}. {UiProjectDirectory} is checked out, so " +
            "the routes the order review skills build have no counterpart in the UI - which is drift, not a missing checkout.");

        var source = File.ReadAllText(path);
        var viewBody = ReadViewObjectBody(source, path);

        var actual = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SHIFT_LIST_ROUTE"] = ReadStringConstant(source, "SHIFT_LIST_ROUTE", path),
            ["SHIFT_LIST_VIEW_QUERY_PARAM"] = ReadStringConstant(source, "SHIFT_LIST_VIEW_QUERY_PARAM", path),
            ["SHIFT_LIST_FROM_QUERY_PARAM"] = ReadStringConstant(source, "SHIFT_LIST_FROM_QUERY_PARAM", path),
            ["SHIFT_LIST_UNTIL_QUERY_PARAM"] = ReadStringConstant(source, "SHIFT_LIST_UNTIL_QUERY_PARAM", path),
            ["SHIFT_LIST_VIEW.Orders"] = ReadMember(viewBody, "Orders", path),
            ["SHIFT_LIST_VIEW.Plannable"] = ReadMember(viewBody, "Plannable", path),
        };

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SHIFT_LIST_ROUTE"] = ShiftListViewRoutes.ShiftListRoute,
            ["SHIFT_LIST_VIEW_QUERY_PARAM"] = ShiftListViewRoutes.ViewQueryParam,
            ["SHIFT_LIST_FROM_QUERY_PARAM"] = ShiftListViewRoutes.FromQueryParam,
            ["SHIFT_LIST_UNTIL_QUERY_PARAM"] = ShiftListViewRoutes.UntilQueryParam,
            ["SHIFT_LIST_VIEW.Orders"] = ShiftListViewRoutes.OrdersView,
            ["SHIFT_LIST_VIEW.Plannable"] = ShiftListViewRoutes.PlannableView,
        };

        var mismatches = expected
            .Where(pair => !string.Equals(actual[pair.Key], pair.Value, StringComparison.Ordinal))
            .Select(pair => $"  {pair.Key}: Klacks.Ui '{actual[pair.Key]}', Klacks.Api ShiftListViewRoutes '{pair.Value}'")
            .ToList();

        mismatches.ShouldBeEmpty(
            "The shift list view constants of Klacks.Ui must carry the same strings as Klacks.Api ShiftListViewRoutes; " +
            $"otherwise the route a skill returns opens the list without its view.{Environment.NewLine}" +
            string.Join(Environment.NewLine, mismatches));
    }

    private static string ReadStringConstant(string source, string name, string path)
    {
        var match = Regex.Match(
            source,
            $@"(?:export\s+)?const\s+{Regex.Escape(name)}\s*(?::[^=]+)?=\s*['""](?<value>[^'""]*)['""]");
        match.Success.ShouldBeTrue($"No string literal const {name} was found in {path}; the guard would compare nothing.");
        return match.Groups["value"].Value;
    }

    private static string ReadViewObjectBody(string source, string path)
    {
        var match = ViewObjectDeclaration.Match(source);
        match.Success.ShouldBeTrue($"No const SHIFT_LIST_VIEW object was found in {path}; the guard would compare nothing.");
        return match.Groups["body"].Value;
    }

    private static string ReadMember(string body, string member, string path)
    {
        var match = Regex.Match(body, $@"\b{Regex.Escape(member)}\s*:\s*['""](?<value>[^'""]*)['""]");
        match.Success.ShouldBeTrue($"SHIFT_LIST_VIEW.{member} has no string literal in {path}; the guard would compare nothing.");
        return match.Groups["value"].Value;
    }
}
