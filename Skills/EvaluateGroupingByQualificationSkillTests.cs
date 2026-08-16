// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the thin evaluate_grouping_by_qualification skill: entityType parsing (valid/invalid)
/// and dispatch of EvaluateGroupingByQualificationQuery. The evaluation logic itself is covered by
/// EvaluateGroupingByQualificationQueryHandlerTests.
/// </summary>

using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateGroupingByQualificationSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static QualificationGroupCandidatesResult Result() => new(
        EntityType: "Employee",
        TotalClientsEvaluated: 5,
        Candidates: [new QualificationGroupCandidate(Guid.NewGuid(), "Erste Hilfe", 3, true)],
        NearThresholdCandidates: [],
        ClientsWithoutValidQualification: 2,
        QualificationsAlreadyCovered: [],
        Recommendation: "1 qualification justifies a new group (Erste Hilfe: 3).");

    [Test]
    public async Task ValidEntityType_SendsQueryAndRelaysRecommendation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<EvaluateGroupingByQualificationQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result());
        var skill = new EvaluateGroupingByQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "Employee" });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Erste Hilfe"));
        await mediator.Received(1).Send(
            Arg.Is<EvaluateGroupingByQualificationQuery>(q => q.EntityType == EntityTypeEnum.Employee),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EntityType_IsParsedCaseInsensitive()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<EvaluateGroupingByQualificationQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result());
        var skill = new EvaluateGroupingByQualificationSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "customer" });

        await mediator.Received(1).Send(
            Arg.Is<EvaluateGroupingByQualificationQuery>(q => q.EntityType == EntityTypeEnum.Customer),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidEntityType_ReturnsErrorWithoutDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new EvaluateGroupingByQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "Manager" });

        Assert.That(result.Success, Is.False);
        await mediator.DidNotReceive().Send(Arg.Any<EvaluateGroupingByQualificationQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void MissingEntityType_Throws()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new EvaluateGroupingByQualificationSkill(mediator);

        Assert.ThrowsAsync<ArgumentException>(
            () => skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));
    }
}
