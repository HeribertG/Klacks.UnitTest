// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds a SealOpenOrdersSkill for tests that only care about the mediator and the job queue: every other dependency
/// is a substitute that grants an administrator unrestricted group scope and answers a fixed company day.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.TestHelpers;

public static class SealOpenOrdersSkillFactory
{
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    public static SealOpenOrdersSkill Create(IMediator mediator, ISealOpenOrdersJobQueue jobQueue)
    {
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(new List<Group>());
        var groupScopeGuard = Substitute.For<IGroupScopeGuard>();
        groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        return new SealOpenOrdersSkill(groupRepository, groupScopeGuard, mediator, companyClock, jobQueue);
    }
}
