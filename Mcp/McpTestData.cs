// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Claims;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Mcp;

public static class McpTestData
{
    public const string TenantIdClaimType = "tenant_id";

    public static SkillDescriptor Descriptor(
        string name,
        SkillCategory category = SkillCategory.Query,
        string executionType = LlmExecutionTypes.Skill,
        IReadOnlyList<SkillParameter>? parameters = null,
        IReadOnlyList<string>? requiredPermissions = null)
    {
        return new SkillDescriptor(
            name,
            $"Description of {name}",
            category,
            parameters ?? Array.Empty<SkillParameter>(),
            requiredPermissions ?? Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null)
        {
            ExecutionType = executionType
        };
    }

    public static ClaimsPrincipal Principal(
        Guid userId,
        Guid tenantId,
        string name = "tester",
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(TenantIdClaimType, tenantId.ToString()),
            new(ClaimTypes.Name, name)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
