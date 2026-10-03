// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the place of UseMcpPermissionCap in Program.cs. The MCP rights ceiling only works after
/// UseAuthorization: UseAuthentication evaluates the default scheme alone (the Identity cookie after AddIdentity),
/// so earlier the principal of a bearer request is still anonymous, and the authorization middleware afterwards
/// replaces HttpContext.User with the uncapped principal of the /mcp policy schemes. That exact misplacement
/// shipped once and let an Admin's token reach Admin-only role checks over MCP. The runtime proof lives in
/// Klacks.IntegrationTest/Mcp/McpPermissionCapPipelineTests; this source scan keeps the order from regressing
/// in a plain unit-test run.
/// </summary>

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class McpPermissionCapPipelineOrderGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string ProgramFileName = "Program.cs";
    private const string UseAuthorizationCall = "app.UseAuthorization();";
    private const string UseMcpPermissionCapCall = "app.UseMcpPermissionCap();";
    private const string UseEndpointsCall = "app.UseEndpoints(";

    [Test]
    public void UseMcpPermissionCap_RunsOnceAfterUseAuthorizationAndBeforeTheEndpoints()
    {
        var source = File.ReadAllText(Path.Combine(LocateApiProject(), ProgramFileName));

        var authorization = source.IndexOf(UseAuthorizationCall, StringComparison.Ordinal);
        var cap = source.IndexOf(UseMcpPermissionCapCall, StringComparison.Ordinal);
        var endpoints = source.IndexOf(UseEndpointsCall, StringComparison.Ordinal);

        Assert.That(authorization, Is.GreaterThanOrEqualTo(0), $"{UseAuthorizationCall} not found in {ProgramFileName}.");
        Assert.That(cap, Is.GreaterThanOrEqualTo(0), $"{UseMcpPermissionCapCall} not found in {ProgramFileName}.");
        Assert.That(endpoints, Is.GreaterThanOrEqualTo(0), $"{UseEndpointsCall} not found in {ProgramFileName}.");
        Assert.That(
            source.IndexOf(UseMcpPermissionCapCall, cap + UseMcpPermissionCapCall.Length, StringComparison.Ordinal),
            Is.EqualTo(-1),
            $"{UseMcpPermissionCapCall} must be registered exactly once.");
        Assert.That(cap, Is.GreaterThan(authorization),
            $"{UseMcpPermissionCapCall} must come after {UseAuthorizationCall}; before it the MCP principal is not final.");
        Assert.That(cap, Is.LessThan(endpoints),
            $"{UseMcpPermissionCapCall} must come before {UseEndpointsCall}.");
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (File.Exists(Path.Combine(candidate, ProgramFileName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
