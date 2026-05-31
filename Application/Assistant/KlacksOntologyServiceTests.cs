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
}
