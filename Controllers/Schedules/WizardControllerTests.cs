// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Controllers.Schedules;

[TestFixture]
public class WizardControllerTests
{
    private IWizardJobRunner _runner = null!;
    private IWizardApplyService _applyService = null!;
    private WizardController _sut = null!;

    [SetUp]
    public void Setup()
    {
        _runner = Substitute.For<IWizardJobRunner>();
        _applyService = Substitute.For<IWizardApplyService>();
        _sut = new WizardController(_runner, _applyService);
    }

    [Test]
    public async Task Start_ReturnsJobId()
    {
        var expectedJobId = Guid.NewGuid();
        _runner
            .StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedJobId);

        var result = await _sut.Start(
            new StartWizardRequest(
                new DateOnly(2026, 4, 20),
                new DateOnly(2026, 4, 30),
                new[] { Guid.NewGuid() },
                null,
                null),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeOfType<StartWizardResponse>().Which.JobId.Should().Be(expectedJobId);
    }

    [Test]
    public void Cancel_ReturnsCancelledFlag()
    {
        var jobId = Guid.NewGuid();
        _runner.TryCancel(jobId).Returns(true);

        var result = _sut.Cancel(new CancelWizardRequest(jobId));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeOfType<CancelWizardResponse>().Which.Cancelled.Should().BeTrue();
    }

    [Test]
    public async Task Apply_ReturnsCreatedWorkIds()
    {
        var jobId = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _applyService.ApplyAsync(jobId, Arg.Any<CancellationToken>()).Returns(ids);

        var result = await _sut.Apply(new ApplyWizardRequest(jobId), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeOfType<ApplyWizardResponse>().Which.CreatedWorkIds.Should().BeEquivalentTo(ids);
    }

    [Test]
    public async Task Apply_ReturnsNotFound_WhenCacheEmpty()
    {
        var jobId = Guid.NewGuid();
        _applyService
            .ApplyAsync(jobId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Guid>>(_ => throw new InvalidOperationException("No cached result"));

        var result = await _sut.Apply(new ApplyWizardRequest(jobId), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }
}
