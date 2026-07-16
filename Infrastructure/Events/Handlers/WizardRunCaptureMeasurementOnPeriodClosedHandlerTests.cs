// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class WizardRunCaptureMeasurementOnPeriodClosedHandlerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private IWizardRunCaptureRepository _repository = null!;
    private IWizardRunCaptureMeasurementService _measurementService = null!;
    private WizardRunCaptureMeasurementOnPeriodClosedHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IWizardRunCaptureRepository>();
        _measurementService = Substitute.For<IWizardRunCaptureMeasurementService>();
        _sut = new WizardRunCaptureMeasurementOnPeriodClosedHandler(
            _repository, _measurementService,
            Substitute.For<ILogger<WizardRunCaptureMeasurementOnPeriodClosedHandler>>());
    }

    private static PeriodClosedEvent SealEvent() => new(
        new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), GroupId, 10, 2, 30, "admin");

    private static WizardRunCapture Capture() => new()
    {
        Id = Guid.NewGuid(),
        Engine = WizardEngine.TokenEvolution,
        ApplyKind = WizardApplyKind.Scenario,
        PeriodFrom = new DateOnly(2026, 4, 1),
        PeriodUntil = new DateOnly(2026, 4, 30),
    };

    [Test]
    public async Task HandleAsync_MeasuresEachUnmeasuredCaptureAsAccepted()
    {
        var c1 = Capture();
        var c2 = Capture();
        _repository.GetUnmeasuredForSealAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), GroupId, Arg.Any<CancellationToken>())
            .Returns(new[] { c1, c2 });

        await _sut.HandleAsync(SealEvent());

        await _measurementService.Received(1).MeasureAsync(c1, CaptureOutcome.Accepted, Arg.Any<CancellationToken>());
        await _measurementService.Received(1).MeasureAsync(c2, CaptureOutcome.Accepted, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_OneCaptureThrows_StillMeasuresOthersAndDoesNotRethrow()
    {
        var failing = Capture();
        var healthy = Capture();
        _repository.GetUnmeasuredForSealAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), GroupId, Arg.Any<CancellationToken>())
            .Returns(new[] { failing, healthy });
        _measurementService.MeasureAsync(failing, Arg.Any<CaptureOutcome>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(SealEvent()));

        await _measurementService.Received(1).MeasureAsync(healthy, CaptureOutcome.Accepted, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_NoUnmeasuredCaptures_DoesNothing()
    {
        _repository.GetUnmeasuredForSealAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WizardRunCapture>());

        await _sut.HandleAsync(SealEvent());

        await _measurementService.DidNotReceiveWithAnyArgs()
            .MeasureAsync(default!, default, default);
    }
}
