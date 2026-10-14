// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioFollowUpSkills: which selected skills need list_scenarios next to them because they take a scenarioId.
/// Live trigger 2026-09-30 (marketing video "Klacksy plant die Woche"): after start_autowizard, "Ja, übernimm es."
/// ran without list_scenarios and the model could not reach the scenario it had just produced.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ScenarioFollowUpSkillsTests
{
    [TestCase("accept_scenario", true)]
    [TestCase("reject_scenario", true)]
    [TestCase("evaluate_scenario", true)]
    [TestCase("start_autowizard", false)]
    [TestCase("list_scenarios", false)]
    public void NeedsScenarioList_OnlyForSkillsThatTakeAScenarioId(string selected, bool expected)
    {
        ScenarioFollowUpSkills.NeedsScenarioList([selected]).ShouldBe(expected);
    }
}
