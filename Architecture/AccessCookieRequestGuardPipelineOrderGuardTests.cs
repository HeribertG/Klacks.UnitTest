// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the place of AccessCookieRequestGuardMiddleware in Program.cs. The middleware is the global CSRF/CSWSH
/// protection of requests authenticated by the klacks_access cookie; static analysis cannot see it, so a syntax-tree
/// scan pins it. JwtBearer (and with it the cookie fallback) only runs inside the authorization middleware, so the
/// guard has to be registered by exactly one top-level statement after UseAuthentication and UseAuthorization and
/// before UseEndpoints (MapControllers and the hubs live inside it). Comments and inactive #if code are trivia of
/// the syntax tree and therefore never count; a registration nested in a lambda, an if or a UseWhen branch is not a
/// top-level statement and fails the guard.
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AccessCookieRequestGuardPipelineOrderGuardTests
{
    private const string ProgramFileName = "Program.cs";
    private const string PipelineVariable = "app";
    private const string UseAuthenticationMethod = "UseAuthentication";
    private const string UseAuthorizationMethod = "UseAuthorization";
    private const string UseEndpointsMethod = "UseEndpoints";
    private const string UseMiddlewareMethod = "UseMiddleware";
    private const string MapControllersMethod = "MapControllers";
    private const string GuardTypeName = "AccessCookieRequestGuardMiddleware";

    [Test]
    public void AccessCookieRequestGuard_IsRegisteredOnceAsTopLevelStatementAfterAuthorizationAndBeforeTheEndpoints()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRootLocator.ApiProject, ProgramFileName));
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        var statements = ((CompilationUnitSyntax)root).Members
            .OfType<GlobalStatementSyntax>()
            .Select(global => global.Statement)
            .OfType<ExpressionStatementSyntax>()
            .ToList();

        var authentication = IndexOfPipelineCall(statements, UseAuthenticationMethod);
        var authorization = IndexOfPipelineCall(statements, UseAuthorizationMethod);
        var endpoints = IndexOfPipelineCall(statements, UseEndpointsMethod);
        var guards = statements
            .Select((statement, index) => (statement, index))
            .Where(entry => IsGuardRegistration(entry.statement))
            .Select(entry => entry.index)
            .ToList();
        var guardRegistrationsAnywhere = root.DescendantNodes().OfType<GenericNameSyntax>()
            .Count(name => name.Identifier.ValueText == UseMiddlewareMethod && IsGuardTypeArgument(name));

        Assert.That(authentication, Is.GreaterThanOrEqualTo(0), $"Top-level app.{UseAuthenticationMethod}() not found in {ProgramFileName}.");
        Assert.That(authorization, Is.GreaterThan(authentication), $"Top-level app.{UseAuthorizationMethod}() must follow app.{UseAuthenticationMethod}().");
        Assert.That(endpoints, Is.GreaterThan(authorization), $"Top-level app.{UseEndpointsMethod}(...) must follow app.{UseAuthorizationMethod}().");
        Assert.That(
            statements[endpoints].DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == MapControllersMethod),
            Is.True,
            $"{MapControllersMethod}() not found inside app.{UseEndpointsMethod}(...).");
        Assert.That(guards.Count, Is.EqualTo(1),
            $"{GuardTypeName} must be registered by exactly one top-level 'app.{UseMiddlewareMethod}<{GuardTypeName}>();' statement.");
        Assert.That(guardRegistrationsAnywhere, Is.EqualTo(1),
            $"{GuardTypeName} is registered additionally in a nested position (lambda, if, branch).");
        Assert.That(guards[0], Is.GreaterThan(authorization),
            $"{GuardTypeName} must come after app.{UseAuthenticationMethod}() and app.{UseAuthorizationMethod}(); before it the cookie fallback of JwtBearer has not run.");
        Assert.That(guards[0], Is.LessThan(endpoints),
            $"{GuardTypeName} must come before app.{UseEndpointsMethod}(...) (MapControllers, hubs).");
    }

    private static int IndexOfPipelineCall(List<ExpressionStatementSyntax> statements, string method)
    {
        return statements.FindIndex(statement => PipelineCall(statement)?.Name.Identifier.ValueText == method);
    }

    private static bool IsGuardRegistration(ExpressionStatementSyntax statement)
    {
        return PipelineCall(statement)?.Name is GenericNameSyntax name
            && name.Identifier.ValueText == UseMiddlewareMethod
            && IsGuardTypeArgument(name);
    }

    private static bool IsGuardTypeArgument(GenericNameSyntax name)
    {
        return name.TypeArgumentList.Arguments.Count == 1
            && name.TypeArgumentList.Arguments[0] is IdentifierNameSyntax type
            && type.Identifier.ValueText == GuardTypeName;
    }

    private static MemberAccessExpressionSyntax? PipelineCall(ExpressionStatementSyntax statement)
    {
        return statement.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            && access.Expression is IdentifierNameSyntax { Identifier.ValueText: PipelineVariable }
            ? access
            : null;
    }
}
