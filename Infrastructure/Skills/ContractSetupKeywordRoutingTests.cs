// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deterministic entry into the contract-setup interview: everyday requests for a new contract
/// must put the contract-setup knowledge skill into the keyword guarantee of the real seed catalog, so the
/// interview and the template comparison are reachable even when vector retrieval misses them.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ContractSetupKeywordRoutingTests
{
    private const string ContractSetupSkill = "explain_contract_setup";
    private const string SeedFileName = "skill-seeds.json";

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };

    [TestCase("Ich brauche einen neuen Vertrag Teilzeit 80 % Zürich")]
    [TestCase("Bitte einen neuen Vertrag für eine Pflegefachperson anlegen")]
    [TestCase("Ich brauche einen Vertrag für die neue Aushilfe")]
    [TestCase("I need a new contract for a part-time nurse")]
    [TestCase("We need a contract for our weekend staff")]
    public void EverydayNewContractRequest_GuaranteesContractSetupSkill(string message)
    {
        var skills = LoadSeedSkills();

        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, message);

        guaranteed.ShouldContain(ContractSetupSkill);
    }

    private static List<AgentSkill> LoadSeedSkills()
    {
        var path = RepositoryRootLocator.FindFile("Klacks.Api", "Application", "Skills", "Definitions", SeedFileName);
        path.ShouldNotBeNull();

        var seed = JsonSerializer.Deserialize<SkillSeedFile>(File.ReadAllText(path), JsonReadOptions);

        return seed!.Skills
            .Select(definition => new AgentSkill
            {
                Name = definition.Name,
                Category = definition.Category,
                TriggerKeywords = TriggerKeywordFormat.Write(definition.TriggerKeywords),
                Synonyms = definition.Synonyms
            })
            .ToList();
    }
}
