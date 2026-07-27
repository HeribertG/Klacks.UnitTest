// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the curated ontology: the world-model block must carry the planning entity facts
/// (Membership planning horizon, replacement token inheritance, effective-limits pointer to the read
/// skills, absence catalog vs. event, shift status lifecycle) plus the cross-cutting rules, and it
/// must stay inside its token budget.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Ontology;

namespace Klacks.UnitTest.Application.Assistant;

[TestFixture]
public class KlacksOntologyServiceTests
{
    private static KlacksOntologyService Service() => new();

    [Test]
    public void Membership_PlanningHorizon_IsInConstraints()
    {
        Service().GetConstraints("Membership")
            .ShouldContain(c => c.Contains("ValidFrom") && c.Contains("planning horizon"));
    }

    [Test]
    public void WorkChange_ReplacementTokenInheritance_IsInConstraints()
    {
        Service().GetConstraints("WorkChange")
            .ShouldContain(c => c.Contains("ReplaceClientId") && c.Contains("scenario token"));
    }

    [Test]
    public void EffectiveLimits_PointToReadSkills_NotFixedNumbers()
    {
        Service().GetConstraints("ClientPeriodHours")
            .ShouldContain(c => c.Contains("get_scheduling_defaults") && c.Contains("list_scheduling_rules"));
    }

    [Test]
    public void WorldModelBlock_RendersTheNewPlanningFacts()
    {
        var block = Service().RenderWorldModelBlock();

        block.ShouldContain("Membership.ValidFrom");
        block.ShouldContain("inherits the parent Work's scenario token");
        block.ShouldContain("ScheduleCommand");
    }

    [Test]
    public void EveryRelationKey_IsAKnownEntity()
    {
        var sut = Service();
        var entities = sut.GetEntities().ToHashSet();

        foreach (var entity in sut.GetEntities())
        {
            // Relations are keyed and iterated via GetEntities, so a key outside the entity list would
            // silently never render. Each relation endpoint must also be a known entity.
            foreach (var relation in sut.GetRelations(entity))
            {
                entities.ShouldContain(relation.From, $"Relation From '{relation.From}' is not a known entity.");
                entities.ShouldContain(relation.To, $"Relation To '{relation.To}' is not a known entity.");
            }
        }
    }

    [Test]
    public void EveryEntityWithConstraints_IsRenderedInTheBlock()
    {
        var sut = Service();
        var block = sut.RenderWorldModelBlock();

        // A constraint authored under a typo'd / unlisted entity key would never reach the block.
        foreach (var entity in sut.GetEntities())
        {
            if (sut.GetConstraints(entity).Count > 0)
            {
                block.ShouldContain($"- {entity}");
            }
        }
    }

    [Test]
    public void Absence_IsDeclaredAsCatalog_AndBreakAsTheEvent()
    {
        Service().GetConstraints("Absence")
            .ShouldContain(c => c.Contains("CATALOG") && c.Contains("the event is Break"));
        Service().GetConstraints("Break")
            .ShouldContain(c => c.Contains("AbsenceId"));
    }

    [Test]
    public void Shift_StatusLifecycle_ForbidsOriginalShiftNextToItsSplits()
    {
        Service().GetConstraints("Shift")
            .ShouldContain(c => c.Contains("never both") && c.Contains("double-books"));
    }

    [Test]
    public void Communication_TypeEnum_IsMarkedAsNonContiguous()
    {
        Service().GetConstraints("Communication")
            .ShouldContain(c => c.Contains("NOT contiguous") && c.Contains("EmergencyPhone"));
    }

    [Test]
    public void GroupItem_DeleteVersusClose_IsInConstraints()
    {
        Service().GetConstraints("GroupItem")
            .ShouldContain(c => c.Contains("strict") && c.Contains("future-relative"));
    }

    [Test]
    public void ClientAvailability_PositivePerDaySemantics_AndPrecedence_AreInConstraints()
    {
        var constraints = Service().GetConstraints("ClientAvailability");

        constraints.ShouldContain(c => c.Contains("POSITIVE") && c.Contains("PER DAY"));
        constraints.ShouldContain(c => c.Contains("Break > ScheduleCommand keyword > availability"));
    }

    [Test]
    public void WorldModelBlock_CarriesTheGlobalRules_SoftDeleteScenarioAndMultiLanguage()
    {
        var block = Service().RenderWorldModelBlock();

        block.ShouldContain("soft-deleted");
        block.ShouldContain("AnalyseToken");
        block.ShouldContain("MultiLanguage");
    }

    [Test]
    public void RenderWorldModelBlock_DropsTheGlobalRules_WhenTheBudgetIsTooSmallForThem()
    {
        // The per-entity facts outrank the cross-cutting preamble: on a tiny budget the block must still
        // be a valid document, not one that spends everything on the preamble.
        var block = Service().RenderWorldModelBlock(maxTokens: 100);

        block.ShouldNotContain("MultiLanguage");
        block.ShouldContain("=== KLACKS WORLD MODEL ===");
        block.ShouldContain("=== END WORLD MODEL ===");
    }

    [Test]
    public void RenderWorldModelBlock_RespectsTokenBudget_AndTruncatesAtBoundary()
    {
        var sut = Service();

        var block = sut.RenderWorldModelBlock(maxTokens: 100);

        (block.Length / 4).ShouldBeLessThanOrEqualTo(100);
        block.ShouldContain("=== KLACKS WORLD MODEL ===");
        block.ShouldContain("=== END WORLD MODEL ===");
        block.ShouldContain("truncated");
        // Boundary-safe: never cut inside a constraint line.
        foreach (var line in block.Split('\n'))
        {
            if (line.StartsWith("  ! ") && !line.Contains("truncated"))
            {
                line.Length.ShouldBeGreaterThan(4);
            }
        }
    }

    [Test]
    public void RenderWorldModelBlock_DefaultBudget_RendersAllEntities_NoTruncationNote()
    {
        var sut = Service();

        var block = sut.RenderWorldModelBlock();

        block.ShouldNotContain("truncated");
        foreach (var entity in sut.GetEntities())
        {
            block.ShouldContain($"- {entity}");
        }
    }
}
