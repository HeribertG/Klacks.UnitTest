// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ContractExpiringSoonDetector — covers no-expiring, expiring without
/// follow-up, expiring with follow-up (skip), and severity mapping per daysUntilExpiry.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ContractExpiringSoonDetectorTests
{
    private IClientContractReadRepository _repo = null!;
    private ContractExpiringSoonDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IClientContractReadRepository>();
        _sut = new ContractExpiringSoonDetector(_repo, NullLogger<ContractExpiringSoonDetector>.Instance);
    }

    private static ClientContract MakeContract(Guid clientId, DateOnly? untilDate, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ClientId = clientId,
        ContractId = Guid.NewGuid(),
        FromDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-365)),
        UntilDate = untilDate,
        IsActive = true,
        Client = new Client { Id = clientId, FirstName = "Max", Name = "Müller" }
    };

    [Test]
    public async Task DetectAsync_NoExpiring_ReturnsEmpty()
    {
        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ExpiringWithoutFollowUp_EmitsEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var clientId = Guid.NewGuid();
        var contract = MakeContract(clientId, today.AddDays(5));

        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });
        _repo.GetContractsForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var expiring = events.Single() as ContractExpiringSoonTriggerEvent;
        Assert.That(expiring!.DaysUntilExpiry, Is.EqualTo(5));
        Assert.That(expiring.Severity, Is.EqualTo("high"));
    }

    [Test]
    public async Task DetectAsync_ExpiringWithFollowUp_Skips()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var clientId = Guid.NewGuid();
        var expiringContract = MakeContract(clientId, today.AddDays(5));
        var followUp = MakeContract(clientId, today.AddDays(400));
        followUp.FromDate = today.AddDays(5);

        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { expiringContract });
        _repo.GetContractsForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { expiringContract, followUp });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ExpiringIn25Days_HasMediumSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var clientId = Guid.NewGuid();
        var contract = MakeContract(clientId, today.AddDays(25));

        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });
        _repo.GetContractsForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });

        var events = await _sut.DetectAsync();

        var expiring = events.Single() as ContractExpiringSoonTriggerEvent;
        Assert.That(expiring!.Severity, Is.EqualTo("medium"));
    }
}
