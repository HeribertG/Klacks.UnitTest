// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The alias table the function executor normalizes with and the toolset membership guard resolves through:
/// an alias maps to its skill, the lookup ignores case, and a real skill name is not an alias.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class SkillNameAliasesTests
{
    [TestCase("get_current_user", "get_user_context")]
    [TestCase("getUserPermissions", "get_user_permissions")]
    [TestCase("create_client", "create_employee")]
    [TestCase("search_clients", "search_employees")]
    [TestCase("navigate_to_page", "navigate_to")]
    [TestCase("CREATE_CLIENT", "create_employee")]
    public void Alias_ResolvesToItsSkill(string alias, string skill)
    {
        SkillNameAliases.TryResolve(alias, out var resolved).ShouldBeTrue();
        resolved.ShouldBe(skill);
        SkillNameAliases.Resolve(alias).ShouldBe(skill);
    }

    [TestCase(null)]
    [TestCase("")]
    public void NullOrEmptyName_IsNotAnAlias_AndDoesNotThrow(string? name)
    {
        SkillNameAliases.TryResolve(name, out var resolved).ShouldBeFalse();
        resolved.ShouldBeEmpty();
        SkillNameAliases.Resolve(name).ShouldBeEmpty();
    }

    [Test]
    public void GetCurrentUser_ResolvesToUserContext_NotToPermissions()
    {
        SkillNameAliases.Resolve("get_current_user").ShouldBe("get_user_context");
    }

    [Test]
    public void RealSkillNameGetUserContext_IsNotRemapped()
    {
        SkillNameAliases.TryResolve("get_user_context", out _).ShouldBeFalse();
        SkillNameAliases.TryResolve("get_user_permissions", out _).ShouldBeFalse();
    }

    [TestCase("create_employee")]
    [TestCase("navigate_to")]
    [TestCase("unknown_skill")]
    public void RealSkillName_IsNotAnAlias_AndResolvesToItself(string name)
    {
        SkillNameAliases.TryResolve(name, out var resolved).ShouldBeFalse();
        resolved.ShouldBeEmpty();
        SkillNameAliases.Resolve(name).ShouldBe(name);
    }
}
