// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Keeps every scenario token that reaches a controller inside the reach of ScenarioTokenAccessFilter, the global MVC
/// filter that refuses tokens of scenarios the caller may not see (2026-10-11; before that the plan view and the
/// scenario writes took any analyseToken from the client unchecked).
///
/// The filter finds tokens by convention only: a simple action parameter named "analyseToken" and Guid/Guid?
/// properties named "AnalyseToken" inside bound Klacks DTOs, down to ScenarioTokenArgumentScanner.MaxDepth. So a
/// future endpoint slips through when it names its token differently, binds it outside the action arguments or nests
/// it deeper. These tests fail in each of those cases:
/// 1. the filter is registered globally in Program.cs;
/// 2. no action parameter and no property reachable from one is a Guid whose name mentions a token but is not the
///    convention name (rename it, or extend the scanner);
/// 3. every token property reachable from an action parameter lies within MaxDepth, and the scanner sees that type;
/// 4. no string-typed action parameter or reachable property mentions a token, apart from the listed authentication
///    and OAuth members (NonScenarioStringTokens) - a scenario token bound as string would bypass the filter.
///
/// Not covered: controllers outside the Klacks.Api assembly (plugins), tokens hidden in dictionaries or headers, and
/// minimal-API endpoints (none carry a scenario token today).
/// </summary>

using System.Reflection;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Extensions;
using Klacks.Api.Presentation.Filters;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ScenarioTokenFilterCoverageGuardTests
{
    private const string ProgramFileName = "Program.cs";
    private const string FilterRegistration = "options.Filters.AddKlacksGlobalFilters()";
    private const string TokenNameFragment = "token";
    private const string ScannableNamespacePrefix = "Klacks.";
    private const int MinimumTokenCarryingParameters = 10;

    /// <summary>
    /// Guid tokens that are not scenario tokens. ClientImportRequest.Token is the id of the upload session of the
    /// employee import (preview/commit of the same file), it never selects a scenario.
    /// </summary>
    private static readonly HashSet<string> NonScenarioTokens = ["ClientImportRequest.Token"];

    /// <summary>
    /// String-typed members whose name mentions a token but that never select a scenario, keyed as
    /// "Type.Property" or "Controller.Action(parameter)". All of them are authentication material or OAuth metadata:
    /// - PasswordResetController.ResetPassword(token), ClientResource.PasswortResetToken, AppUser.PasswordResetToken,
    ///   ResetPasswordResource.Token, ValidatePasswordResetTokenResource.Token, ChangePasswordResource.Token: Identity
    ///   password-reset tokens;
    /// - RefreshRequestResource.Token / .RefreshToken: the JWT and refresh token of the session refresh;
    /// - IdentityProviderResource.TokenUrl, OAuthClientRegistrationRequest.TokenEndpointAuthMethod: OAuth endpoint URL and
    ///   client-authentication method (configuration text, not a token value).
    /// </summary>
    private static readonly HashSet<string> NonScenarioStringTokens =
    [
        "PasswordResetController.ResetPassword(token)",
        "ClientResource.PasswortResetToken",
        "AppUser.PasswordResetToken",
        "ResetPasswordResource.Token",
        "ValidatePasswordResetTokenResource.Token",
        "ChangePasswordResource.Token",
        "RefreshRequestResource.Token",
        "RefreshRequestResource.RefreshToken",
        "IdentityProviderResource.TokenUrl",
        "OAuthClientRegistrationRequest.TokenEndpointAuthMethod"
    ];

    [Test]
    public void TheFilterIsRegisteredGlobally()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRootLocator.ApiProject, ProgramFileName));

        source.ShouldContain(FilterRegistration, customMessage:
            "Program.cs must register the global MVC filters through AddKlacksGlobalFilters.");

        var filters = new FilterCollection().AddKlacksGlobalFilters();

        filters.OfType<TypeFilterAttribute>().Select(filter => filter.ImplementationType)
            .ShouldContain(typeof(ScenarioTokenAccessFilter),
                "ScenarioTokenAccessFilter must stay a global MVC filter; without it every endpoint accepts any analyseToken.");
    }

    [Test]
    public void EveryTokenReachableFromAnActionFollowsTheConventionTheFilterReads()
    {
        var offenders = new List<string>();

        foreach (var (action, parameter) in ActionParameters())
        {
            var where = $"{action.DeclaringType!.Name}.{action.Name}({parameter.Name})";
            if (IsGuid(parameter.ParameterType)
                && parameter.Name!.Contains(TokenNameFragment, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parameter.Name, ScenarioTokenArgumentScanner.TokenName, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{where}: parameter '{parameter.Name}'");
            }

            offenders.AddRange(MisnamedTokenProperties(parameter.ParameterType).Select(p => $"{where}: property {p}"));
        }

        offenders.Distinct().ShouldBeEmpty(
            "Guid tokens the filter cannot see. Name them 'analyseToken'/'AnalyseToken' or teach ScenarioTokenArgumentScanner the new form.");
    }

    [Test]
    public void NoStringTokenReachableFromAnActionEscapesTheFilter()
    {
        var offenders = new List<string>();

        foreach (var (action, parameter) in ActionParameters())
        {
            var where = $"{action.DeclaringType!.Name}.{action.Name}({parameter.Name})";
            if (parameter.ParameterType == typeof(string)
                && parameter.Name!.Contains(TokenNameFragment, StringComparison.OrdinalIgnoreCase)
                && !NonScenarioStringTokens.Contains($"{action.DeclaringType!.Name}.{action.Name}({parameter.Name})"))
            {
                offenders.Add($"{where}: string parameter '{parameter.Name}'");
            }

            offenders.AddRange(StringTokenProperties(parameter.ParameterType).Select(p => $"{where}: string property {p}"));
        }

        offenders.Distinct().ShouldBeEmpty(
            "String tokens the filter cannot see (it only reads Guid analyseToken). Bind a scenario token as Guid named "
            + "'analyseToken'/'AnalyseToken'; list a string that is not a scenario token in NonScenarioStringTokens with the reason.");
    }

    [Test]
    public void EveryTokenPropertyReachableFromAnActionLiesWithinTheScannerDepth()
    {
        var carrying = ActionParameters()
            .Select(ap => ap.Parameter.ParameterType)
            .Distinct()
            .Where(HasTokenPropertyInGraph)
            .ToList();

        carrying.Count.ShouldBeGreaterThan(MinimumTokenCarryingParameters,
            "Too few token-carrying parameter types found - the scan is not reading the real controllers.");

        var offenders = carrying
            .Where(t => !ScenarioTokenArgumentScanner.CanCarryToken(t)
                || ScenarioTokenArgumentScanner.MaxTokenDepth(t) > ScenarioTokenArgumentScanner.MaxDepth)
            .Select(t => $"{t.FullName}: depth {ScenarioTokenArgumentScanner.MaxTokenDepth(t)}")
            .ToList();

        offenders.ShouldBeEmpty($"Token properties beyond ScenarioTokenArgumentScanner.MaxDepth ({ScenarioTokenArgumentScanner.MaxDepth}) are not checked.");
    }

    private static IEnumerable<(MethodInfo Action, ParameterInfo Parameter)> ActionParameters() =>
        typeof(BaseController).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => !m.IsSpecialName && typeof(ControllerBase).IsAssignableFrom(m.DeclaringType)
                && m.DeclaringType != typeof(ControllerBase) && m.DeclaringType != typeof(Controller)
                && m.GetCustomAttribute<NonActionAttribute>() is null)
            .SelectMany(m => m.GetParameters().Select(p => (m, p)));

    private static bool IsGuid(Type type) => type == typeof(Guid) || type == typeof(Guid?);

    private static IEnumerable<string> StringTokenProperties(Type root) =>
        GraphProperties(root)
            .Where(p => p.PropertyType == typeof(string)
                && p.Name.Contains(TokenNameFragment, StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .Where(name => !NonScenarioStringTokens.Contains(name));

    private static bool HasTokenPropertyInGraph(Type root) =>
        GraphProperties(root).Any(p => p.Name == ScenarioTokenArgumentScanner.TokenName && IsGuid(p.PropertyType));

    private static IEnumerable<string> MisnamedTokenProperties(Type root) =>
        GraphProperties(root)
            .Where(p => IsGuid(p.PropertyType)
                && p.Name.Contains(TokenNameFragment, StringComparison.OrdinalIgnoreCase)
                && p.Name != ScenarioTokenArgumentScanner.TokenName)
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .Where(name => !NonScenarioTokens.Contains(name));

    private static IEnumerable<PropertyInfo> GraphProperties(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            if (ElementType(type) is { } element)
            {
                queue.Enqueue(element);
                continue;
            }

            if (type.IsPrimitive || type.IsEnum || type.Namespace?.StartsWith(ScannableNamespacePrefix, StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                yield return property;
                queue.Enqueue(property.PropertyType);
            }
        }
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }
}
