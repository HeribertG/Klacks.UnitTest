// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deterministic entry into the target-hours skills and their separation from the absence-hours skills and
/// the contract knowledge: "Sollstunden" questions reach the target-hours knowledge, calculation or advice, "Ferien-
/// stunden" questions keep reaching the absence-hours knowledge, and a plain contract question keeps explain_contracts.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class TargetHoursKeywordRoutingTests
{
    private const string ExplainSkill = "explain_target_hours";
    private const string CalculationSkill = "get_target_hours_calculation";
    private const string AdviceSkill = "evaluate_target_hours_setup";
    private const string AbsenceExplainSkill = "explain_absence_hours";
    private const string ContractsSkill = "explain_contracts";

    [TestCase("Wie werden die Sollstunden von Anna Müller berechnet?")]
    [TestCase("Woher kommen die Sollstunden eines Mitarbeiters ohne Vertrag?")]
    [TestCase("Where do the target hours come from for an employee without a contract?")]
    [TestCase("How are the target hours of an employee calculated?")]
    public void TargetHoursQuestion_GuaranteesTheKnowledgeSkill(string message)
    {
        SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message).ShouldContain(ExplainSkill);
    }

    [TestCase("Zeig mir die Berechnung der Sollstunden von Anna Müller für November 2026.")]
    [TestCase("Why does Hans Muster have 184 target hours in January?")]
    [TestCase("Show the target hours calculation for Hans Muster for 2026-11.")]
    public void TargetHoursOfAPerson_GuaranteesTheCalculationSkill(string message)
    {
        SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message).ShouldContain(CalculationSkill);
    }

    [TestCase("Die Sollstunden stimmen nicht bei Anna Müller, welcher Vertrag passt?")]
    [TestCase("Bitte die Sollstunden-Tabelle prüfen.")]
    [TestCase("Anna's target hours are wrong, which contract fits a 60 percent workload?")]
    public void TargetHoursComplaint_GuaranteesTheAdviceSkill(string message)
    {
        SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message).ShouldContain(AdviceSkill);
    }

    [TestCase("Warum bekommt Hans Muster nur 7.56 Stunden pro Ferientag?")]
    [TestCase("Wie viele Urlaubsstunden zählt ein Tag bei 80 Prozent?")]
    public void VacationHoursQuestion_KeepsTheAbsenceHoursSkill_AndNotTheTargetHoursOne(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(AbsenceExplainSkill);
        guaranteed.ShouldNotContain(ExplainSkill);
        guaranteed.ShouldNotContain(CalculationSkill);
    }

    [TestCase("Was steht im Vertrag Vollzeit 160 zu den Sollstunden?")]
    [TestCase("Welche Sollstunden gibt es im Januar?")]
    public void ContractOrTableQuestion_KeepsItsExistingSkills(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(ContractsSkill);
        guaranteed.ShouldContain("list_monthly_target_hours");
    }
}
