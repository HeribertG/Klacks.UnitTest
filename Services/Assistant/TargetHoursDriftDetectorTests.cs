// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TargetHoursDriftDetector — verifies threshold gating and severity mapping
/// from drift magnitude (≥12h medium, ≥24h high).
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TargetHoursDriftDetectorTests
{
    private IClientRepository _clientRepository = null!;
    private IWorkRepository _workRepository = null!;
    private TargetHoursDriftDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _sut = new TargetHoursDriftDetector(_clientRepository, _workRepository,
            NullLogger<TargetHoursDriftDetector>.Instance);
    }

    private static Client MakeClient(string firstName = "Anna") => new()
    {
        Id = Guid.NewGuid(),
        FirstName = firstName,
        Name = "Müller"
    };

    [Test]
    public async Task DetectAsync_NoClients_ReturnsEmpty()
    {
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_WithinThreshold_Skips()
    {
        var client = MakeClient();
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 152, GuaranteedHours = 160 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_NegativeDrift_OverThreshold_EmitsHigh()
    {
        var client = MakeClient();
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 130, GuaranteedHours = 160 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var drift = events.Single() as TargetHoursDriftTriggerEvent;
        Assert.That(drift!.DriftHours, Is.EqualTo(-30m));
        Assert.That(drift.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_NoGuaranteedHours_Skips()
    {
        var client = MakeClient();
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 100, GuaranteedHours = 0 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
