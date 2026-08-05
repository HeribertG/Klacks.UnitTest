// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Services.Schedules.AutoWizard;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

/// <summary>
/// A broadcast that cannot be delivered must not travel back into the caller: the run itself finished,
/// and letting the exception through would report it as failed.
/// </summary>
[TestFixture]
public sealed class AutoWizardHubNotifierTests
{
    private IHubContext<AutoWizardJobHub, IAutoWizardJobClient> _hubContext = null!;
    private IAutoWizardJobClient _client = null!;
    private AutoWizardHubNotifier _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _hubContext = Substitute.For<IHubContext<AutoWizardJobHub, IAutoWizardJobClient>>();
        _client = Substitute.For<IAutoWizardJobClient>();
        var clients = Substitute.For<IHubClients<IAutoWizardJobClient>>();
        clients.Group(Arg.Any<string>()).Returns(_client);
        _hubContext.Clients.Returns(clients);

        _sut = new AutoWizardHubNotifier(_hubContext, NullLogger<AutoWizardHubNotifier>.Instance);
    }

    private static AutoWizardJobResultDto Result(Guid jobId) => new(
        JobId: jobId,
        FinalScenarioId: Guid.NewGuid(),
        FinalScenarioToken: Guid.NewGuid(),
        FinalScenarioName: "Auto",
        ElapsedMs: 1234,
        QualificationGaps: [],
        ComplianceViolations: [],
        ComplianceSkippedPlacements: []);

    private static AutoWizardJobFailureDto Failure(Guid jobId) => new(
        jobId, "Harmonizer", "boom", null, null, null);

    [Test]
    public async Task NotifyCompletedAsync_BroadcastThrows_IsSwallowed()
    {
        var jobId = Guid.NewGuid();
        _client.OnCompleted(Arg.Any<AutoWizardJobResultDto>()).ThrowsAsync(new InvalidOperationException("hub down"));

        await Should.NotThrowAsync(() => _sut.NotifyCompletedAsync(jobId, Result(jobId)));
    }

    [Test]
    public async Task NotifyFailedAsync_BroadcastThrows_IsSwallowed()
    {
        var jobId = Guid.NewGuid();
        _client.OnFailed(Arg.Any<AutoWizardJobFailureDto>()).ThrowsAsync(new InvalidOperationException("hub down"));

        await Should.NotThrowAsync(() => _sut.NotifyFailedAsync(Failure(jobId)));
    }

    [Test]
    public async Task NotifyFailedAsync_SendsTheFailurePayload()
    {
        var jobId = Guid.NewGuid();
        var failure = Failure(jobId);

        await _sut.NotifyFailedAsync(failure);

        await _client.Received(1).OnFailed(failure);
    }

    [Test]
    public async Task NotifyCompletedAsync_SendsTheResult()
    {
        var jobId = Guid.NewGuid();
        var dto = Result(jobId);

        await _sut.NotifyCompletedAsync(jobId, dto);

        await _client.Received(1).OnCompleted(dto);
    }
}
