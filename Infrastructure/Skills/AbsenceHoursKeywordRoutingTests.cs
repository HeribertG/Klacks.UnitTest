// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deterministic entry into the absence-hours skills: everyday questions about how many hours a
/// vacation day credits must put the knowledge skill, the calculation skill or the setup advice into the keyword
/// guarantee of the real seed catalog, so they are reachable even when vector retrieval misses them. The negative
/// cases keep the calendar-value explanation (explain_absence_value_vs_hours) from being shadowed.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class AbsenceHoursKeywordRoutingTests
{
    private const string ExplainSkill = "explain_absence_hours";
    private const string CalculationSkill = "get_absence_hours_calculation";
    private const string AdviceSkill = "evaluate_absence_hours_setup";
    private const string ValueVsHoursSkill = "explain_absence_value_vs_hours";

    [TestCase("Warum bekommt Hans Muster nur 7.56 Stunden pro Ferientag?")]
    [TestCase("Wie werden Absenzstunden berechnet und wo stelle ich das ein?")]
    [TestCase("Wie viele Urlaubsstunden zählt ein Tag bei 80 Prozent?")]
    [TestCase("How are absence hours calculated in Klacks?")]
    [TestCase("Why does a part-time employee get fewer hours per vacation day?")]
    public void EverydayAbsenceHoursQuestion_GuaranteesTheKnowledgeSkill(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(ExplainSkill);
    }

    [TestCase("Zeig mir die Berechnung der Ferienstunden von Anna Müller am 3.11.2026.")]
    [TestCase("Bitte die Rechnung offenlegen: Ferienstunden von Hans Muster am 2. November.")]
    [TestCase("Show me the absence hours calculation for Anna Müller on 2026-11-03.")]
    [TestCase("Calculate vacation hours of Hans Muster for 2026-11-02.")]
    public void CalculationRequest_GuaranteesTheCalculationSkill(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(CalculationSkill);
    }

    [TestCase("Anna Müller hat zu wenig Ferienstunden, was muss ich im Vertrag ändern?")]
    [TestCase("Die Ferienstunden stimmen nicht bei Hans Muster.")]
    [TestCase("Anna's vacation hours are wrong, which field do I change?")]
    [TestCase("Hans gets too few vacation hours per day.")]
    public void SetupComplaint_GuaranteesTheAdviceSkill(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(AdviceSkill);
    }

    [TestCase("Der Wert stimmt nicht mit Stunden überein - woher kommen die Absenz-Stunden in der Liste?")]
    [TestCase("Where do absence hours come from in the calendar list?")]
    public void CalendarValueQuestion_StillGuaranteesTheValueVsHoursSkill(string message)
    {
        var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(SeedSkillCatalog.Load(), message);

        guaranteed.ShouldContain(ValueVsHoursSkill);
    }
}
