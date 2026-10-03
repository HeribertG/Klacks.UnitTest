// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Microsoft.Extensions.Caching.Memory;

namespace Klacks.UnitTest.Application.Services.Schedules.PlanningRules;

/// <summary>The presence answer is cached until a handler invalidates it.</summary>
[TestFixture]
public class PlanningConstraintPresenceTests
{
    [Test]
    public async Task Answer_IsCached_UntilInvalidated()
    {
        var repository = Substitute.For<IPlanningConstraintRepository>();
        repository.AnyApprovedAsync(Arg.Any<CancellationToken>()).Returns(false, true);
        var presence = new PlanningConstraintPresence(repository, new MemoryCache(new MemoryCacheOptions()));

        (await presence.AnyApprovedAsync()).ShouldBeFalse();
        (await presence.AnyApprovedAsync()).ShouldBeFalse();
        presence.Invalidate();
        (await presence.AnyApprovedAsync()).ShouldBeTrue();

        await repository.Received(2).AnyApprovedAsync(Arg.Any<CancellationToken>());
    }
}
