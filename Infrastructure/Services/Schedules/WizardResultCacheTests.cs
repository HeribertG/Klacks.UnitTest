// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardResultCacheTests
{
    [Test]
    public void Store_Then_TryGet_ReturnsScenarioAndToken()
    {
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        var scenario = new CoreScenario { Id = "s" };
        var token = Guid.NewGuid();

        cache.Store(jobId, scenario, token);

        cache.TryGet(jobId, out var retrieved, out var analyseToken, out _).ShouldBeTrue();
        retrieved.ShouldBeSameAs(scenario);
        analyseToken.ShouldBe(token);
    }

    [Test]
    public void Store_WithNullToken_ReturnsNullToken()
    {
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        var scenario = new CoreScenario { Id = "s" };

        cache.Store(jobId, scenario, null);

        cache.TryGet(jobId, out _, out var analyseToken, out _).ShouldBeTrue();
        analyseToken.ShouldBeNull();
    }

    [Test]
    public void TryGet_ReturnsFalse_ForUnknownId()
    {
        var cache = new WizardResultCache();

        cache.TryGet(Guid.NewGuid(), out var scenario, out var analyseToken, out _).ShouldBeFalse();
        scenario.ShouldBeNull();
        analyseToken.ShouldBeNull();
    }

    [Test]
    public void Invalidate_RemovesEntry()
    {
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, new CoreScenario { Id = "s" }, null);

        cache.Invalidate(jobId);

        cache.TryGet(jobId, out _, out _, out _).ShouldBeFalse();
    }
}
