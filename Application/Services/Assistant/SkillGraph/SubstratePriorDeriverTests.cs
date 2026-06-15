// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the substrate-prior deriver: two skills sharing a domain entity get a co-required
/// derived candidate edge (ordinally ordered), skills on different entities stay unlinked, derivation
/// is idempotent, unmapped skills are ignored, and edges never cross agent boundaries.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SubstratePriorDeriverTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    private static SubstratePriorDeriver Build(IAgentSkillRepository skillRepo, ISkillRelationRepository relationRepo)
        => new(skillRepo, relationRepo, NullLogger<SubstratePriorDeriver>.Instance);

    private static AgentSkill Skill(string name, Guid agentId) => new() { AgentId = agentId, Name = name };

    [Test]
    public async Task Derive_LinksTwoSkillsSharingAnEntity_AsCoRequiredDerivedCandidate()
    {
        var skillRepo = Substitute.For<IAgentSkillRepository>();
        skillRepo.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            Skill("add_client_note", AgentId),   // Client
            Skill("add_client_email", AgentId),  // Client, Communication
        });
        var relationRepo = Substitute.For<ISkillRelationRepository>();
        relationRepo.GetByAgentAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());
        List<SkillRelation>? added = null;
        relationRepo.When(r => r.AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>()))
            .Do(ci => added = ci.Arg<IEnumerable<SkillRelation>>().ToList());

        await Build(skillRepo, relationRepo).DeriveAsync();

        added.ShouldNotBeNull();
        added!.Count.ShouldBe(1);
        var edge = added[0];
        edge.SkillAName.ShouldBe("add_client_email");
        edge.SkillBName.ShouldBe("add_client_note");
        edge.Type.ShouldBe(SkillRelationType.CoRequired);
        edge.Source.ShouldBe(SkillRelationSource.Derived);
        edge.Status.ShouldBe(SkillRelationStatus.Candidate);
        edge.Confidence.ShouldBe(SkillGraphConstants.SubstratePriorConfidence);
        edge.Provenance.ShouldBe(SkillGraphConstants.SubstratePriorProvenance);
        edge.AgentId.ShouldBe(AgentId);
    }

    [Test]
    public async Task Derive_DoesNotLinkSkillsOnDifferentEntities()
    {
        var skillRepo = Substitute.For<IAgentSkillRepository>();
        skillRepo.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            Skill("add_client_note", AgentId),  // Client
            Skill("add_break", AgentId),        // Break
        });
        var relationRepo = Substitute.For<ISkillRelationRepository>();
        relationRepo.GetByAgentAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

        await Build(skillRepo, relationRepo).DeriveAsync();

        await relationRepo.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Derive_IsIdempotent_DoesNotReAddExistingEdge()
    {
        var skillRepo = Substitute.For<IAgentSkillRepository>();
        skillRepo.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            Skill("add_client_note", AgentId),
            Skill("add_client_email", AgentId),
        });
        var relationRepo = Substitute.For<ISkillRelationRepository>();
        relationRepo.GetByAgentAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>
        {
            new()
            {
                AgentId = AgentId,
                SkillAName = "add_client_email",
                SkillBName = "add_client_note",
                Type = SkillRelationType.CoRequired,
                Source = SkillRelationSource.Derived,
            },
        });

        await Build(skillRepo, relationRepo).DeriveAsync();

        await relationRepo.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Derive_IgnoresSkillsNotInTheEntityMap()
    {
        var skillRepo = Substitute.For<IAgentSkillRepository>();
        skillRepo.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            Skill("add_client_note", AgentId),
            Skill("some_unmapped_utility_skill", AgentId),
        });
        var relationRepo = Substitute.For<ISkillRelationRepository>();
        relationRepo.GetByAgentAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

        await Build(skillRepo, relationRepo).DeriveAsync();

        await relationRepo.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Derive_DoesNotCreateCrossAgentEdges()
    {
        var otherAgent = Guid.NewGuid();
        var skillRepo = Substitute.For<IAgentSkillRepository>();
        skillRepo.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            Skill("add_client_note", AgentId),     // Client, agent A
            Skill("add_client_email", otherAgent), // Client, agent B
        });
        var relationRepo = Substitute.For<ISkillRelationRepository>();
        relationRepo.GetByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

        await Build(skillRepo, relationRepo).DeriveAsync();

        await relationRepo.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }
}
