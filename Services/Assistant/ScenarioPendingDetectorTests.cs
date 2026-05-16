// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScenarioPendingDetector — covers active-only filter, 48h pending threshold,
/// and severity assignment.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ScenarioPendingDetectorTests
{
    private IAnalyseScenarioRepository _repo = null!;
    private ScenarioPendingDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IAnalyseScenarioRepository>();
        _sut = new ScenarioPendingDetector(_repo, NullLogger<ScenarioPendingDetector>.Instance);
    }

    private static AnalyseScenario MakeScenario(AnalyseScenarioStatus status, int hoursOld) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        Token = Guid.NewGuid(),
        Status = status,
        CreateTime = DateTime.UtcNow.AddHours(-hoursOld)
    };

    [Test]
    public async Task DetectAsync_NoScenarios_ReturnsEmpty()
    {
        _repo.GetByGroupAsync(null, Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_RecentScenario_BelowThreshold_Skips()
    {
        _repo.GetByGroupAsync(null, Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>
            {
                MakeScenario(AnalyseScenarioStatus.Active, hoursOld: 24)
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AcceptedScenario_AlwaysSkipped()
    {
        _repo.GetByGroupAsync(null, Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>
            {
                MakeScenario(AnalyseScenarioStatus.Accepted, hoursOld: 200)
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ActiveAndOlderThan48h_EmitsEvent()
    {
        _repo.GetByGroupAsync(null, Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>
            {
                MakeScenario(AnalyseScenarioStatus.Active, hoursOld: 72)
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var pending = events.Single() as ScenarioPendingTriggerEvent;
        Assert.That(pending!.HoursPending, Is.GreaterThanOrEqualTo(72));
        Assert.That(pending.Severity, Is.EqualTo("medium"));
    }
}
