// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deterministic entry into the contract-setup interview: everyday requests for a new contract
/// must put the contract-setup knowledge skill into the keyword guarantee of the real seed catalog, so the
/// interview and the template comparison are reachable even when vector retrieval misses them.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ContractSetupKeywordRoutingTests
{
    private const string ContractSetupSkill = "explain_contract_setup";

    [TestCase("Ich brauche einen neuen Vertrag Teilzeit 80 % Zürich")]
    [TestCase("Bitte einen neuen Vertrag für eine Pflegefachperson anlegen")]
    [TestCase("Ich brauche einen Vertrag für die neue Aushilfe")]
    [TestCase("I need a new contract for a part-time nurse")]
    [TestCase("We need a contract for our weekend staff")]
    public void EverydayNewContractRequest_GuaranteesContractSetupSkill(string message)
    {
        var skills = SeedSkillCatalog.Load();

        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, message);

        guaranteed.ShouldContain(ContractSetupSkill);
    }
}
