// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Application.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardResultCacheTests
{
    [Test]
    public void Store_Then_TryGet_ReturnsScenario()
    {
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        var scenario = new CoreScenario { Id = "s" };

        cache.Store(jobId, scenario);

        cache.TryGet(jobId, out var retrieved).Should().BeTrue();
        retrieved.Should().BeSameAs(scenario);
    }

    [Test]
    public void TryGet_ReturnsFalse_ForUnknownId()
    {
        var cache = new WizardResultCache();

        cache.TryGet(Guid.NewGuid(), out var scenario).Should().BeFalse();
        scenario.Should().BeNull();
    }

    [Test]
    public void Invalidate_RemovesEntry()
    {
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, new CoreScenario { Id = "s" });

        cache.Invalidate(jobId);

        cache.TryGet(jobId, out _).Should().BeFalse();
    }
}
