// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights and shape of evaluate_user_role_change: it is gated on the Admin role like the other advice skills on
/// Admin-only actions, which reaches exactly the roles the right of the user list and of the role assignment reaches
/// (CanEditSettings, Admin only), it is a read-only Query with the Advise effect, and the AdvisesFor edge to the role assignment
/// exists.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class UserRoleChangeSkillSeedTests
{
    private const string AdviceSkill = "evaluate_user_role_change";
    private const string ListSkill = "list_system_users";
    private const string AssignSkill = "assign_user_permissions";
    private const int AdvisesForType = 3;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    [Test]
    public void AdviceSkill_ReachesTheSameRolesAsTheUserListAndTheAssignment()
    {
        var advice = Seeded(AdviceSkill).GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToList();
        var list = Seeded(ListSkill).GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToList();
        var assign = Seeded(AssignSkill).GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToList();

        advice.ShouldBe([Roles.Admin]);
        list.ShouldBe([Permissions.CanEditSettings]);
        assign.ShouldBe([Permissions.CanEditSettings]);
        Permissions.GetPermissionsForRole(Roles.Admin).ShouldContain(Permissions.CanEditSettings);
        Permissions.GetPermissionsForRole(Roles.Authorised).ShouldNotContain(Permissions.CanEditSettings);
        Permissions.PlannerFloor.ShouldNotContain(Permissions.CanEditSettings);
    }

    [Test]
    public void AdviceSkill_IsAReadOnlyQueryWithTheAdviseEffectInTheSecurityArea()
    {
        var skill = Seeded(AdviceSkill);

        skill.GetProperty("category").GetString().ShouldBe("Query");
        skill.GetProperty("effect").GetString().ShouldBe("Advise");
        skill.GetProperty("area").GetString().ShouldBe("security");
        skill.GetProperty("alwaysOn").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public void AdviceSkill_HasAnAdvisesForEdgeToTheRoleAssignment()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-relation-seeds.json")));

        document.RootElement.GetProperty("relations").EnumerateArray().ShouldContain(relation =>
            relation.GetProperty("skillAName").GetString() == AdviceSkill &&
            relation.GetProperty("skillBName").GetString() == AssignSkill &&
            relation.GetProperty("type").GetInt32() == AdvisesForType);
    }

    private static JsonElement Seeded(string skillName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-seeds.json")));

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");
        return skill.Clone();
    }

    private static string Locate(string fileName) =>
        RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, fileName]);
}
