// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the thin evaluate_location_group_candidates skill: entityType parsing (valid/invalid)
/// and dispatch of EvaluateLocationGroupCandidatesQuery. The evaluation logic itself is covered by
/// EvaluateLocationGroupCandidatesQueryHandlerTests.
/// </summary>

using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateLocationGroupCandidatesSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static LocationGroupCandidatesResult Result() => new(
        EntityType: "Employee",
        Candidates: [new LocationGroupCandidate("Winterthur", 3, true)],
        NearThresholdCandidates: [],
        ClientsWithoutUsableAddress: 0,
        ClientsInExistingLocationGroup: 0,
        Recommendation: "1 city justifies a new location group (Winterthur: 3).");

    [Test]
    public async Task ValidEntityType_SendsQueryAndRelaysRecommendation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<EvaluateLocationGroupCandidatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result());
        var skill = new EvaluateLocationGroupCandidatesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "Employee" });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Winterthur"));
        await mediator.Received(1).Send(
            Arg.Is<EvaluateLocationGroupCandidatesQuery>(q => q.EntityType == EntityTypeEnum.Employee),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EntityType_IsParsedCaseInsensitive()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<EvaluateLocationGroupCandidatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result());
        var skill = new EvaluateLocationGroupCandidatesSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "customer" });

        await mediator.Received(1).Send(
            Arg.Is<EvaluateLocationGroupCandidatesQuery>(q => q.EntityType == EntityTypeEnum.Customer),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidEntityType_ReturnsErrorWithoutDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new EvaluateLocationGroupCandidatesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "Manager" });

        Assert.That(result.Success, Is.False);
        await mediator.DidNotReceive().Send(Arg.Any<EvaluateLocationGroupCandidatesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void MissingEntityType_Throws()
    {
        // GetRequiredString throws on a missing parameter (same behavior as ProposeGroupingSkill,
        // which also requires entityType this way) — no SkillResult.Error path for this case.
        var mediator = Substitute.For<IMediator>();
        var skill = new EvaluateLocationGroupCandidatesSkill(mediator);

        Assert.ThrowsAsync<ArgumentException>(
            () => skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));
    }
}
