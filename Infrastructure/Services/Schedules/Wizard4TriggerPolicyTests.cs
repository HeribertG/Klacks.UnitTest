// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Services.Schedules;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class Wizard4TriggerPolicyTests
{
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly Until = new(2026, 6, 30);
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly Wizard4TriggerPolicy _policy = new();

    [Test]
    public void SelectTargets_PicksGroupsNotInCooldown()
    {
        var g = Guid.NewGuid();
        var viewed = new List<Wizard4TriggerTarget> { new(g, From, Until) };

        var selected = _policy.SelectTargets(viewed, new Dictionary<Guid, DateTime>(), Now);

        selected.Count.ShouldBe(1);
        selected[0].GroupId.ShouldBe(g);
    }

    [Test]
    public void SelectTargets_SkipsGroupsStillInCooldown()
    {
        var g = Guid.NewGuid();
        var viewed = new List<Wizard4TriggerTarget> { new(g, From, Until) };
        var cooldown = new Dictionary<Guid, DateTime> { [g] = Now.AddMinutes(10) };

        _policy.SelectTargets(viewed, cooldown, Now).ShouldBeEmpty();
    }

    [Test]
    public void SelectTargets_PicksGroupOnceCooldownExpired()
    {
        var g = Guid.NewGuid();
        var viewed = new List<Wizard4TriggerTarget> { new(g, From, Until) };
        var cooldown = new Dictionary<Guid, DateTime> { [g] = Now.AddMinutes(-1) };

        _policy.SelectTargets(viewed, cooldown, Now).Count.ShouldBe(1);
    }

    [Test]
    public void SelectTargets_DeduplicatesAGroupViewedByMultipleConnections()
    {
        var g = Guid.NewGuid();
        var viewed = new List<Wizard4TriggerTarget>
        {
            new(g, From, Until),
            new(g, From, Until),
        };

        _policy.SelectTargets(viewed, new Dictionary<Guid, DateTime>(), Now).Count.ShouldBe(1);
    }
}
