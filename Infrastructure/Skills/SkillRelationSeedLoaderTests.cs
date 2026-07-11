// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the skill relation seed loader: missing edges are inserted for the default agent
/// with Learned source and seed-prefixed provenance, existing edges are never touched (idempotent),
/// edges referencing unknown skills are skipped, and a missing seed file or default agent is a no-op.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillRelationSeedLoaderTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    private string _contentRoot = string.Empty;
    private IAgentRepository _agentRepository = null!;
    private IAgentSkillRepository _agentSkillRepository = null!;
    private ISkillRelationRepository _skillRelationRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-relation-seed-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "Application", "Skills", "Definitions"));

        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = AgentId });

        _agentSkillRepository = Substitute.For<IAgentSkillRepository>();
        _agentSkillRepository.GetAllByAgentIdAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>
        {
            new() { AgentId = AgentId, Name = "create_shift" },
            new() { AgentId = AgentId, Name = "cut_shift" },
        });

        _skillRelationRepository = Substitute.For<ISkillRelationRepository>();
        _skillRelationRepository.GetByAgentAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private SkillRelationSeedLoader Build()
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);

        return new SkillRelationSeedLoader(
            _skillRelationRepository,
            _agentSkillRepository,
            _agentRepository,
            environment,
            NullLogger<SkillRelationSeedLoader>.Instance);
    }

    private void WriteSeedFile(object seedFile)
    {
        var path = Path.Combine(_contentRoot, "Application", "Skills", "Definitions", "skill-relation-seeds.json");
        File.WriteAllText(path, JsonSerializer.Serialize(seedFile));
    }

    private static object Edge(string a, string b, int type = 0, double confidence = 0.95, int status = 1) => new
    {
        skillAName = a,
        skillBName = b,
        type,
        confidence,
        supportCount = 100,
        contradictionCount = 0,
        provenance = "learned:cooccurrence",
        status,
    };

    [Test]
    public async Task Load_InsertsMissingEdge_AsLearnedWithSeedProvenance()
    {
        WriteSeedFile(new { version = 1, relations = new[] { Edge("create_shift", "cut_shift") } });
        List<SkillRelation>? added = null;
        _skillRelationRepository.When(r => r.AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>()))
            .Do(ci => added = ci.Arg<IEnumerable<SkillRelation>>().ToList());

        await Build().LoadAsync();

        added.ShouldNotBeNull();
        added!.Count.ShouldBe(1);
        var edge = added[0];
        edge.AgentId.ShouldBe(AgentId);
        edge.SkillAName.ShouldBe("create_shift");
        edge.SkillBName.ShouldBe("cut_shift");
        edge.Type.ShouldBe(SkillRelationType.CoRequired);
        edge.Confidence.ShouldBe(0.95);
        edge.SupportCount.ShouldBe(100);
        edge.Source.ShouldBe(SkillRelationSource.Learned);
        edge.Status.ShouldBe(SkillRelationStatus.Active);
        edge.Provenance.ShouldBe(SkillGraphConstants.SeededExperienceProvenancePrefix + "learned:cooccurrence");
    }

    [Test]
    public async Task Load_IsIdempotent_DoesNotReAddExistingEdge()
    {
        WriteSeedFile(new { version = 1, relations = new[] { Edge("create_shift", "cut_shift") } });
        _skillRelationRepository.GetByAgentAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>
        {
            new()
            {
                AgentId = AgentId,
                SkillAName = "create_shift",
                SkillBName = "cut_shift",
                Type = SkillRelationType.CoRequired,
                Source = SkillRelationSource.Learned,
            },
        });

        await Build().LoadAsync();

        await _skillRelationRepository.DidNotReceive()
            .AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Load_SameSkillPairWithDifferentType_IsInserted()
    {
        WriteSeedFile(new { version = 1, relations = new[] { Edge("create_shift", "cut_shift", type: 1) } });
        _skillRelationRepository.GetByAgentAsync(AgentId, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>
        {
            new()
            {
                AgentId = AgentId,
                SkillAName = "create_shift",
                SkillBName = "cut_shift",
                Type = SkillRelationType.CoRequired,
                Source = SkillRelationSource.Learned,
            },
        });
        List<SkillRelation>? added = null;
        _skillRelationRepository.When(r => r.AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>()))
            .Do(ci => added = ci.Arg<IEnumerable<SkillRelation>>().ToList());

        await Build().LoadAsync();

        added.ShouldNotBeNull();
        added!.Count.ShouldBe(1);
        added[0].Type.ShouldBe(SkillRelationType.Sequential);
    }

    [Test]
    public async Task Load_SkipsEdgeReferencingUnknownSkill()
    {
        WriteSeedFile(new { version = 1, relations = new[] { Edge("create_shift", "send_message") } });

        await Build().LoadAsync();

        await _skillRelationRepository.DidNotReceive()
            .AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Load_MissingSeedFile_IsNoOp()
    {
        await Build().LoadAsync();

        await _skillRelationRepository.DidNotReceive()
            .AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Load_NoDefaultAgent_IsNoOp()
    {
        WriteSeedFile(new { version = 1, relations = new[] { Edge("create_shift", "cut_shift") } });
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        await Build().LoadAsync();

        await _skillRelationRepository.DidNotReceive()
            .AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>());
    }
}
