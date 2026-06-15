// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the experience-based skill-relation learner: cold-start guard, co-required
/// reinforcement on positive lift, decay on genuine contradiction, sequential edge creation from
/// adjacency, and exclusion of System/UI utility skills.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillRelationLearnerTests
{
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly DateTime BaseTime = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SkillUsageRecord Usage(string session, string skill, int order, SkillCategory category = SkillCategory.Crud)
        => new()
        {
            SkillName = skill,
            Category = category,
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SessionId = session,
            Success = true,
            Timestamp = BaseTime.AddSeconds(order),
        };

    private static SkillRelation Edge(string a, string b, SkillRelationType type, double confidence,
        SkillRelationStatus status = SkillRelationStatus.Candidate)
        => new()
        {
            AgentId = Agent,
            SkillAName = a,
            SkillBName = b,
            Type = type,
            Confidence = confidence,
            Status = status,
            Source = SkillRelationSource.Derived,
            Provenance = "x",
        };

    private static (SkillRelationLearner Sut, List<SkillRelation> Added, List<SkillRelation> Updated) Build(
        IReadOnlyList<SkillUsageRecord> usage, List<SkillRelation> existing)
    {
        var usageRepo = Substitute.For<ISkillUsageRepository>();
        usageRepo.GetRecordsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(usage);
        var relRepo = Substitute.For<ISkillRelationRepository>();
        relRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(existing);
        var added = new List<SkillRelation>();
        var updated = new List<SkillRelation>();
        relRepo.When(r => r.AddRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>()))
            .Do(ci => added.AddRange(ci.Arg<IEnumerable<SkillRelation>>()));
        relRepo.When(r => r.UpdateRangeAsync(Arg.Any<IEnumerable<SkillRelation>>(), Arg.Any<CancellationToken>()))
            .Do(ci => updated.AddRange(ci.Arg<IEnumerable<SkillRelation>>()));
        return (new SkillRelationLearner(usageRepo, relRepo, NullLogger<SkillRelationLearner>.Instance), added, updated);
    }

    [Test]
    public async Task Learn_BelowMinSessions_DoesNothing()
    {
        var usage = new List<SkillUsageRecord>
        {
            Usage("s1", "aa", 0), Usage("s1", "bb", 1),
            Usage("s2", "aa", 0), Usage("s2", "bb", 1),
        };
        var (sut, added, updated) = Build(usage, new List<SkillRelation> { Edge("aa", "bb", SkillRelationType.CoRequired, 0.4) });

        var result = await sut.LearnAsync();

        result.ShouldBe(0);
        added.ShouldBeEmpty();
        updated.ShouldBeEmpty();
    }

    [Test]
    public async Task Learn_PositiveLift_ReinforcesExistingCoRequiredAndCreatesNewLearnedEdge()
    {
        var usage = new List<SkillUsageRecord>();
        for (var i = 0; i < 4; i++) { usage.Add(Usage($"ab{i}", "aa", 0)); usage.Add(Usage($"ab{i}", "bb", 1)); }
        usage.Add(Usage("ac", "aa", 0)); usage.Add(Usage("ac", "cc", 1));
        usage.Add(Usage("bd", "bb", 0)); usage.Add(Usage("bd", "dd", 1));
        for (var i = 0; i < 4; i++) { usage.Add(Usage($"ef{i}", "ee", 0)); usage.Add(Usage($"ef{i}", "ff", 1)); }

        var (sut, added, updated) = Build(usage, new List<SkillRelation> { Edge("aa", "bb", SkillRelationType.CoRequired, 0.4) });

        await sut.LearnAsync();

        var reinforced = updated.SingleOrDefault(e => e.Type == SkillRelationType.CoRequired && e.SkillAName == "aa" && e.SkillBName == "bb");
        reinforced.ShouldNotBeNull();
        reinforced!.Confidence.ShouldBe(0.45, 0.0001);
        reinforced.SupportCount.ShouldBe(1);
        added.ShouldContain(e => e.Type == SkillRelationType.CoRequired && e.SkillAName == "ee" && e.SkillBName == "ff"
            && e.Source == SkillRelationSource.Learned);
    }

    [Test]
    public async Task Learn_Contradiction_DecaysExistingEdge()
    {
        var usage = new List<SkillUsageRecord>();
        for (var i = 0; i < 5; i++) { usage.Add(Usage($"ax{i}", "aa", 0)); usage.Add(Usage($"ax{i}", "xx", 1)); }
        for (var i = 0; i < 5; i++) { usage.Add(Usage($"by{i}", "bb", 0)); usage.Add(Usage($"by{i}", "yy", 1)); }

        var (sut, added, updated) = Build(usage,
            new List<SkillRelation> { Edge("aa", "bb", SkillRelationType.CoRequired, 0.7, SkillRelationStatus.Active) });

        await sut.LearnAsync();

        var decayed = updated.SingleOrDefault(e => e.Type == SkillRelationType.CoRequired && e.SkillAName == "aa" && e.SkillBName == "bb");
        decayed.ShouldNotBeNull();
        decayed!.Confidence.ShouldBe(0.55, 0.0001);
        decayed.ContradictionCount.ShouldBe(1);
        decayed.Status.ShouldBe(SkillRelationStatus.Candidate);
    }

    [Test]
    public async Task Learn_AdjacencyCreatesSequentialEdge()
    {
        var usage = new List<SkillUsageRecord>();
        for (var i = 0; i < 5; i++) { usage.Add(Usage($"s{i}", "aa", 0)); usage.Add(Usage($"s{i}", "bb", 1)); }

        var (sut, added, updated) = Build(usage, new List<SkillRelation> { Edge("aa", "bb", SkillRelationType.CoRequired, 0.4) });

        await sut.LearnAsync();

        added.ShouldContain(e => e.Type == SkillRelationType.Sequential && e.SkillAName == "aa" && e.SkillBName == "bb"
            && e.Source == SkillRelationSource.Learned && e.Status == SkillRelationStatus.Candidate);
    }

    [Test]
    public async Task Learn_ExcludesSystemSkills()
    {
        var usage = new List<SkillUsageRecord>();
        for (var i = 0; i < 6; i++) { usage.Add(Usage($"s{i}", "sys", 0, SkillCategory.System)); usage.Add(Usage($"s{i}", "tool", 1)); }

        var (sut, added, updated) = Build(usage, new List<SkillRelation> { Edge("aa", "bb", SkillRelationType.CoRequired, 0.4) });

        await sut.LearnAsync();

        added.ShouldNotContain(e => e.SkillAName == "sys" || e.SkillBName == "sys");
        updated.ShouldNotContain(e => e.SkillAName == "sys" || e.SkillBName == "sys");
    }
}
