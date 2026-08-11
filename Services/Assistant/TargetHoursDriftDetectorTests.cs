// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TargetHoursDriftDetector — verifies threshold gating, severity mapping
/// from drift magnitude (≥12h medium, ≥24h high), and that the scanned period is the last
/// completed calendar month rather than the running one.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TargetHoursDriftDetectorTests
{
    private IClientRepository _clientRepository = null!;
    private IWorkRepository _workRepository = null!;
    private TargetHoursDriftDetector _sut = null!;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _sut = CreateSut(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
    }

    private TargetHoursDriftDetector CreateSut(DateTimeOffset now) =>
        new(_clientRepository, _workRepository,
            NullLogger<TargetHoursDriftDetector>.Instance, new FixedTimeProvider(now));

    private static Client MakeClient(string firstName = "Anna", EntityTypeEnum type = EntityTypeEnum.Employee) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = firstName,
        Name = "Müller",
        Type = type
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
    public async Task DetectAsync_CustomerWithDrift_Skips()
    {
        var customer = MakeClient("Clara", EntityTypeEnum.Customer);
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { customer });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [customer.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_MixedRoster_EmitsForStaffOnly()
    {
        var customer = MakeClient("Clara", EntityTypeEnum.Customer);
        var employee = MakeClient("Anna");
        var externEmp = MakeClient("Matteo", EntityTypeEnum.ExternEmp);
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { customer, employee, externEmp });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [customer.Id] = new() { Hours = 0, GuaranteedHours = 170 },
                [employee.Id] = new() { Hours = 0, GuaranteedHours = 170 },
                [externEmp.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(2));
        var clientIds = events.Cast<TargetHoursDriftTriggerEvent>().Select(e => e.ClientId).ToList();
        Assert.That(clientIds, Is.EquivalentTo(new[] { employee.Id, externEmp.Id }));
    }

    [Test]
    public async Task DetectAsync_StaffWithoutContract_StillEmits()
    {
        var employee = MakeClient();
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { employee });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [employee.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
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

    [Test]
    public async Task DetectAsync_ScansLastCompletedMonth_NotTheRunningOne()
    {
        var client = MakeClient();
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        await _workRepository.Received(1).GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(),
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
        var drift = events.Single() as TargetHoursDriftTriggerEvent;
        Assert.That(drift!.PeriodLabel, Is.EqualTo("2026-07"));
    }

    [Test]
    public async Task DetectAsync_InJanuary_ScansDecemberOfPreviousYear()
    {
        var client = MakeClient();
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });
        var sut = CreateSut(new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.Zero));

        var events = await sut.DetectAsync();

        await _workRepository.Received(1).GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(),
            new DateOnly(2025, 12, 1),
            new DateOnly(2025, 12, 31),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
        var drift = events.Single() as TargetHoursDriftTriggerEvent;
        Assert.That(drift!.PeriodLabel, Is.EqualTo("2025-12"));
    }
}
