// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The period-closing skills resolve their optional group through PeriodClosingGroupResolver, which is fail-closed:
/// an unrestricted caller may omit the group and gets the installation-wide answer, a group-restricted caller must
/// name one of their groups and is refused (without reading any data) when they omit it or have no scope at all.
/// </summary>

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PeriodClosingGroupScopeTests
{
    private static readonly Guid SalesRootId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IGroupRepository _groupRepository = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedPeriodSummaryDto>());
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.Get(SalesRootId).Returns(new Group { Id = SalesRootId, Name = "Verkauf" });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static Dictionary<string, object> Range(string? groupId = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["startDate"] = "2026-06-01",
            ["endDate"] = "2026-06-30"
        };
        if (groupId != null)
        {
            parameters["groupId"] = groupId;
        }

        return parameters;
    }

    [Test]
    public async Task UnrestrictedCaller_WithoutGroup_GetsTheInstallationWideAnswer()
    {
        var skill = new GetPeriodStatusSkill(_mediator, _groupRepository, TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<GetSealedPeriodsQuery>(q => q.GroupId == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedCaller_WithoutGroup_IsAskedForOneOfTheirGroups_AndNothingIsRead()
    {
        var skill = new GetPeriodStatusSkill(
            _mediator, _groupRepository, TestGroupScopeGuard.Restricted(new[] { SalesRootId }, "Verkauf"));

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Verkauf");
        await _mediator.DidNotReceive().Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedCallerWithoutScope_WithoutGroup_IsRefused()
    {
        var skill = new GetPeriodStatusSkill(
            _mediator, _groupRepository, TestGroupScopeGuard.Restricted(Array.Empty<Guid>()));

        var result = await skill.ExecuteAsync(Ctx(), Range());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no group is assigned to you");
        await _mediator.DidNotReceive().Send(Arg.Any<GetSealedPeriodsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedCaller_WithOwnGroup_IsServed()
    {
        var skill = new GetPeriodStatusSkill(
            _mediator, _groupRepository, TestGroupScopeGuard.Restricted(new[] { SalesRootId }, "Verkauf"));

        var result = await skill.ExecuteAsync(Ctx(), Range(SalesRootId.ToString()));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<GetSealedPeriodsQuery>(q => q.GroupId == SalesRootId), Arg.Any<CancellationToken>());
    }
}
