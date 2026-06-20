// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the WP-P0.1 soul-section seed: the new always-on domain_expertise section is
/// seeded with the planning semantics (effective-limits pointer, Membership.ValidFrom, soft-delete,
/// scenario isolation, guardrail awareness), and a second seed run is idempotent (no re-upsert when
/// the seeded content is unchanged). Idempotency is proven by capture-and-replay so the test does
/// not duplicate the section literals.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Assistant;

[TestFixture]
public class AgentSoulSectionSeedServiceTests
{
    private const string SeedSource = "seed";

    private IAgentRepository _agents = null!;
    private IAgentSoulRepository _soul = null!;
    private Agent _agent = null!;
    private List<(string Type, string Content, int Order)> _captured = null!;

    [SetUp]
    public void Setup()
    {
        _agent = new Agent { Id = Guid.NewGuid() };
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);

        _soul = Substitute.For<IAgentSoulRepository>();
        _soul.GetActiveSectionsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSoulSection>());

        _captured = new List<(string, string, int)>();
        _soul.UpsertSectionAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _captured.Add((ci.ArgAt<string>(1), ci.ArgAt<string>(2), ci.ArgAt<int>(3)));
                return new AgentSoulSection();
            });
    }

    private AgentSoulSectionSeedService Service()
        => new(_agents, _soul, Substitute.For<ILogger<AgentSoulSectionSeedService>>());

    [Test]
    public async Task Seeds_DomainExpertise_WithPlanningSemantics()
    {
        await Service().SeedAsync();

        _captured.ShouldContain(c =>
            c.Type == SoulSectionTypes.DomainExpertise
            && c.Content.Contains("get_scheduling_defaults")
            && c.Content.Contains("Membership.ValidFrom")
            && c.Content.Contains("soft-deleted")
            && c.Content.Contains("pre-commit validated"));
    }

    [Test]
    public async Task Seeds_Humor_WithGlobalConservativeGuardrails()
    {
        await Service().SeedAsync();

        _captured.ShouldContain(c =>
            c.Type == SoulSectionTypes.Humor
            && c.Content.Contains("self-deprecating")
            && c.Content.Contains("No sarcasm")
            && c.Content.Contains("religion, politics")
            && c.Content.Contains("EVERY language and culture")
            && c.Content.Contains("[USER_MOOD: FRUSTRATED]"));
    }

    [Test]
    public async Task SecondRun_IsIdempotent_NoReUpsert()
    {
        await Service().SeedAsync();

        var existing = _captured
            .Select(c => new AgentSoulSection
            {
                SectionType = c.Type,
                Content = c.Content,
                SortOrder = c.Order,
                Source = SeedSource,
                IsActive = true
            })
            .ToList();
        _soul.GetActiveSectionsAsync(_agent.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _soul.ClearReceivedCalls();

        await Service().SeedAsync();

        await _soul.DidNotReceive().UpsertSectionAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
