// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardJobRegistryTests
{
    [Test]
    public void Register_ReturnsLinkedCts_AndReportsRunning()
    {
        var registry = new WizardJobRegistry();
        var jobId = Guid.NewGuid();

        var cts = registry.Register(jobId, CancellationToken.None);

        cts.ShouldNotBeNull();
        registry.IsRunning(jobId).ShouldBeTrue();
    }

    [Test]
    public void TryCancel_CancelsToken_AndReturnsTrue()
    {
        var registry = new WizardJobRegistry();
        var jobId = Guid.NewGuid();
        var cts = registry.Register(jobId, CancellationToken.None);

        var cancelled = registry.TryCancel(jobId);

        cancelled.ShouldBeTrue();
        cts.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void TryCancel_ReturnsFalse_ForUnknownJob()
    {
        var registry = new WizardJobRegistry();

        registry.TryCancel(Guid.NewGuid()).ShouldBeFalse();
    }

    [Test]
    public void Remove_DropsEntry()
    {
        var registry = new WizardJobRegistry();
        var jobId = Guid.NewGuid();
        registry.Register(jobId, CancellationToken.None);

        registry.Remove(jobId);

        registry.IsRunning(jobId).ShouldBeFalse();
    }
}
