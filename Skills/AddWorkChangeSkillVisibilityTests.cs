// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Visibility tests for AddWorkChangeSkill: a replacement client the caller may not see by group visibility
/// is answered exactly like an unknown replacement client id, a work of a hidden client exactly like an unknown
/// work, and no WorkChange is posted.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddWorkChangeSkillVisibilityTests
{
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid WorkClientId = Guid.NewGuid();
    private static readonly Guid ReplaceClientId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IWorkRepository _workRepository = null!;
    private IClientRepository _clientRepository = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientRepository = Substitute.For<IClientRepository>();

        _workRepository.Get(WorkId).Returns(new Work { Id = WorkId, ClientId = WorkClientId });
        _clientRepository.Exists(ReplaceClientId).Returns(true);
        _mediator.Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkChangeResource { Id = Guid.NewGuid() });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(Guid replaceClientId) => new()
    {
        ["workId"] = WorkId.ToString(),
        ["type"] = "ReplacementWithin",
        ["startTime"] = "10:00",
        ["endTime"] = "12:00",
        ["replaceClientId"] = replaceClientId.ToString()
    };

    [Test]
    public async Task HiddenReplacementClient_AnswersLikeUnknownId_WithoutPosting()
    {
        var guard = SkillClientVisibility.Hiding(ReplaceClientId);
        var skill = new AddWorkChangeSkill(_mediator, _workRepository, _clientRepository, guard);
        var unknownId = Guid.NewGuid();

        var hidden = await skill.ExecuteAsync(Ctx(), Params(ReplaceClientId));
        var unknown = await skill.ExecuteAsync(Ctx(), Params(unknownId));

        hidden.Success.ShouldBeFalse();
        unknown.Success.ShouldBeFalse();
        SkillClientVisibility.WithoutId(hidden.Message, ReplaceClientId)
            .ShouldBe(SkillClientVisibility.WithoutId(unknown.Message, unknownId));
        await guard.Received().IsVisibleAsync(ReplaceClientId, Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WorkOfHiddenClient_AnswersLikeUnknownWork_WithoutPosting()
    {
        var skill = new AddWorkChangeSkill(_mediator, _workRepository, _clientRepository, SkillClientVisibility.Hiding(WorkClientId));
        var unknownWorkId = Guid.NewGuid();
        var unknownParams = Params(ReplaceClientId);
        unknownParams["workId"] = unknownWorkId.ToString();

        var hidden = await skill.ExecuteAsync(Ctx(), Params(ReplaceClientId));
        var unknown = await skill.ExecuteAsync(Ctx(), unknownParams);

        hidden.Success.ShouldBeFalse();
        SkillClientVisibility.WithoutId(hidden.Message, WorkId)
            .ShouldBe(SkillClientVisibility.WithoutId(unknown.Message, unknownWorkId));
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VisibleReplacementClient_PostsWorkChange()
    {
        var skill = new AddWorkChangeSkill(_mediator, _workRepository, _clientRepository, SkillClientVisibility.AllVisible());

        var result = await skill.ExecuteAsync(Ctx(), Params(ReplaceClientId));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c => c.Resource.ReplaceClientId == ReplaceClientId),
            Arg.Any<CancellationToken>());
    }
}
