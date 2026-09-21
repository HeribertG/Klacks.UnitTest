// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the three client-contract seed permissions to the rule the write path actually enforces. All
/// three skills mutate Client.ClientContracts and save the whole aggregate with PUT api/backend/Clients,
/// so PutCommandHandler is the gate they have to agree with — and since the owner decision of 21.09.2026
/// that gate is CanEditContracts, not the Admin role: assigning, re-dating and detaching a person's
/// contract is supervisor work, while the contract TEMPLATE stays administrative (create_contract keeps
/// CanCreateContracts, delete_contract keeps CanDeleteContracts).
///
/// The seed is the only autorising difference between the two roles here: ClientsController.Put carries
/// no role restriction at all, so a seed that still said "Admin" would hide from a supervisor a tool the
/// endpoint would have accepted. Remember the version bump when changing a permission — SkillSeedLoader
/// skips a seed whose version did not increase, and the change would be silently ignored in every
/// existing database.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

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
            ["delete_contract"] = Permissions.CanDeleteContracts
        };

    [TestCaseSource(nameof(ContractMutatingSkills))]
    public void ContractAssignmentSkill_RequiresExactlyTheContractEditingRight(string skillName)
    {
        var permissions = PermissionsOf(skillName);

        permissions.ShouldBe(
            new List<string> { Permissions.CanEditContracts },
            $"'{skillName}' changes the contract ASSIGNMENT of one person, which PutCommandHandler allows " +
            "to every caller holding CanEditContracts since the owner decision of 21.09.2026. A wider " +
            "entry (the Admin role) hides the tool from a supervisor the endpoint would accept; a " +
            "different one lets the assistant propose a call the handler then refuses.");
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(DefinitionsRelativePath).Concat([fileName]).ToArray());

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} above the test directory.");
    }
}
