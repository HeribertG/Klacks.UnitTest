// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the three client-contract seed permissions to the rule the write path actually enforces. All
/// three skills mutate Client.ClientContracts and save the whole aggregate with PUT api/backend/Clients,
/// so PutCommandHandler is the gate they have to agree with — and since the owner decision of 2026-11-07
/// (roles are work areas) that gate is CanEditClients: assigning, re-dating and detaching a person's
/// contract is employee maintenance and supervisor work, while the contract TEMPLATE is a setting and
/// Admin-only (create_contract keeps CanCreateContracts, delete_contract keeps CanDeleteContracts, and
/// the Authorised role no longer expands to any of the Can*Contracts write rights).
///
/// The seed is the only autorising difference between the two roles here: ClientsController.Put carries
/// no role restriction at all, so a seed that still said "Admin" would hide from a supervisor a tool the
/// endpoint would have accepted. Remember the version bump when changing a permission — SkillSeedLoader
/// skips a seed whose version did not increase, and the change would be silently ignored in every
/// existing database.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ClientContractSkillPermissionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    /// <summary>Skills whose write touches Client.ClientContracts and therefore hits PutCommandHandler.</summary>
    private static readonly string[] ContractMutatingSkills =
    [
        "assign_contract_to_client",
        "assign_contract_by_name",
        "remove_client_contract"
    ];

    /// <summary>The templates, which stay administrative and must not drift onto the assignment right.</summary>
    private static readonly Dictionary<string, string> ContractTemplateSkills =
        new(StringComparer.Ordinal)
        {
            ["create_contract"] = Permissions.CanCreateContracts,
            ["create_contract_from_template"] = Permissions.CanCreateContracts,
            ["delete_contract"] = Permissions.CanDeleteContracts
        };

    [TestCaseSource(nameof(ContractMutatingSkills))]
    public void ContractAssignmentSkill_RequiresExactlyTheEmployeeEditingRight(string skillName)
    {
        var permissions = PermissionsOf(skillName);

        permissions.ShouldBe(
            new List<string> { Permissions.CanEditClients },
            $"'{skillName}' changes the contract ASSIGNMENT of one person, which PutCommandHandler allows " +
            "to every caller holding CanEditClients since the owner decision of 2026-11-07. The template " +
            "right CanEditContracts is Admin-only now and would hide the tool from a supervisor the " +
            "endpoint accepts; a different one lets the assistant propose a call the handler then refuses.");
    }

    [TestCaseSource(nameof(ContractMutatingSkills))]
    public void ContractAssignmentSkill_IsHeldByASupervisorButNotByThePlannerFloor(string skillName)
    {
        var permissions = PermissionsOf(skillName);

        permissions.All(Permissions.ExpandRoles(new[] { Roles.Authorised }).Contains).ShouldBeTrue(
            $"'{skillName}' is employee maintenance, which the Supervisor may do.");
        permissions.All(Permissions.PlannerFloor.Contains).ShouldBeFalse(
            $"'{skillName}' would be reachable by a caller without a role.");
    }

    [TestCaseSource(nameof(ContractTemplateSkills))]
    public void ContractTemplateSkill_IsAdminOnly(KeyValuePair<string, string> skill)
    {
        Permissions.ExpandRoles(new[] { Roles.Authorised }).ShouldNotContain(
            skill.Value,
            $"'{skill.Key}' writes a contract template, which is a setting and Admin-only (owner decision 2026-11-07).");
    }

    [TestCaseSource(nameof(ContractTemplateSkills))]
    public void ContractTemplateSkill_KeepsItsOwnRight(KeyValuePair<string, string> skill)
    {
        PermissionsOf(skill.Key).ShouldBe(
            new List<string> { skill.Value },
            $"'{skill.Key}' writes the contract TEMPLATE (master data), not one person's assignment. " +
            "Opening the templates was explicitly not part of the assignment decision.");
    }

    private static List<string> PermissionsOf(string skillName)
    {
        var json = File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName));
        using var document = JsonDocument.Parse(json);

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");

        return skill.GetProperty("requiredPermissions")
            .EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToList();
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        return RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, fileName]);
    }
}
