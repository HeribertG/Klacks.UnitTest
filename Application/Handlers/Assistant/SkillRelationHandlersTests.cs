// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Phase 3 (visibility) tests for the skill-relation handlers: the list query drops retired edges
/// and orders by confidence; accept boosts confidence and promotes to active; dismiss drops
/// confidence and counts a contradiction; both commands return null for an unknown edge.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class SkillRelationHandlersTests
{
    private static SkillRelation Edge(double confidence, SkillRelationStatus status,
        SkillRelationType type = SkillRelationType.CoRequired)
        => new()
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            SkillAName = "aa",
            SkillBName = "bb",
            Type = type,
            Confidence = confidence,
            Status = status,
            Source = SkillRelationSource.Learned,
            Provenance = "x",
        };

    [Test]
    public async Task GetSkillRelations_DropsRetired_AndOrdersByConfidenceDescending()
    {
        var repo = Substitute.For<ISkillRelationRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>
        {
            Edge(0.3, SkillRelationStatus.Candidate),
            Edge(0.0, SkillRelationStatus.Retired),
            Edge(0.9, SkillRelationStatus.Active),
        });

        var result = await new GetSkillRelationsQueryHandler(repo).Handle(new GetSkillRelationsQuery(), default);

        result.Count.ShouldBe(2);
        result[0].Confidence.ShouldBe(0.9);
        result[1].Confidence.ShouldBe(0.3);
    }

    [Test]
    public async Task Accept_BoostsConfidence_PromotesToActive_AndIncrementsSupport()
    {
        var edge = Edge(0.5, SkillRelationStatus.Candidate);
        var repo = Substitute.For<ISkillRelationRepository>();
        repo.GetByIdAsync(edge.Id, Arg.Any<CancellationToken>()).Returns(edge);

        var dto = await new AcceptSkillRelationCommandHandler(repo).Handle(new AcceptSkillRelationCommand(edge.Id), default);

        dto.ShouldNotBeNull();
        dto!.Confidence.ShouldBe(0.7, 0.0001);
        dto.Status.ShouldBe("Active");
        dto.SupportCount.ShouldBe(1);
        await repo.Received(1).UpdateAsync(edge, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Dismiss_DropsConfidence_AndCountsContradiction()
    {
        var edge = Edge(0.5, SkillRelationStatus.Active);
        var repo = Substitute.For<ISkillRelationRepository>();
        repo.GetByIdAsync(edge.Id, Arg.Any<CancellationToken>()).Returns(edge);

        var dto = await new DismissSkillRelationCommandHandler(repo).Handle(new DismissSkillRelationCommand(edge.Id), default);

        dto.ShouldNotBeNull();
        dto!.Confidence.ShouldBe(0.2, 0.0001);
        dto.Status.ShouldBe("Candidate");
        dto.ContradictionCount.ShouldBe(1);
    }

    [Test]
    public async Task Accept_UnknownEdge_ReturnsNull()
    {
        var repo = Substitute.For<ISkillRelationRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((SkillRelation?)null);

        var dto = await new AcceptSkillRelationCommandHandler(repo).Handle(new AcceptSkillRelationCommand(Guid.NewGuid()), default);

        dto.ShouldBeNull();
    }

    [Test]
    public async Task Dismiss_UnknownEdge_ReturnsNull()
    {
        var repo = Substitute.For<ISkillRelationRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((SkillRelation?)null);

        var dto = await new DismissSkillRelationCommandHandler(repo).Handle(new DismissSkillRelationCommand(Guid.NewGuid()), default);

        dto.ShouldBeNull();
    }
}
