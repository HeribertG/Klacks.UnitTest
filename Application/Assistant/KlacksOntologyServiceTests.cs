// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the WP-P0.1 ontology constraint additions: the always-on world-model block must
/// carry the planning entity facts (Membership planning horizon, replacement token inheritance,
/// effective-limits pointer to the read skills).
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
