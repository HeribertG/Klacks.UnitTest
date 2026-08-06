// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins one seed permission to the rule the endpoint actually enforces. PutCommandHandler refuses to
/// let a non-admin change a client's contracts at all — it throws "Only administrators can modify
/// client contracts" — so a contract skill offered to Authorised would be offered and then fail with a
/// 400 the user cannot act on. Before these skills wrote through the REST API they bypassed that rule
/// entirely, which is exactly the kind of gap the unification is meant to close; this test makes sure
/// it does not silently reopen.
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

    /// <summary>Skills whose write touches Client.ClientContracts and therefore hits the admin guard.</summary>
    private static readonly string[] ContractMutatingSkills =
    [
        "assign_contract_to_client",
        "assign_contract_by_name",
        "remove_client_contract"
    ];

    [TestCaseSource(nameof(ContractMutatingSkills))]
    public void ContractMutatingSkill_IsOfferedToAdminsOnly(string skillName)
    {
        var json = File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName));
        using var document = JsonDocument.Parse(json);

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");

        var permissions = skill.GetProperty("requiredPermissions")
            .EnumerateArray().Select(p => p.GetString()).ToList();

        permissions.ShouldContain(
            Roles.Admin,
            $"'{skillName}' changes client contracts, which PutCommandHandler allows to admins only. " +
            "Offering it more widely means the assistant proposes a tool whose call then fails. " +
            "Remember the version bump when changing this — SkillSeedLoader skips a seed whose version " +
            "did not increase.");

        permissions.ShouldNotContain(
            Permissions.CanEditClients,
            $"'{skillName}' must not be reachable through CanEditClients — Authorised holds it, and the " +
            "endpoint would refuse the write.");
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
