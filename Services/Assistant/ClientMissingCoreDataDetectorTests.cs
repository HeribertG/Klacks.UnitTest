// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientMissingCoreDataDetector — covers the empty result, the defensive
/// skip when nothing is missing, one event per missing field with the contracted severity
/// and dedup key, and the per-tick emission cap.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ClientMissingCoreDataDetectorTests
{
    private IClientCoreDataReadRepository _repo = null!;
    private ClientMissingCoreDataDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IClientCoreDataReadRepository>();
        _sut = CreateSut(new DateOnly(2026, 1, 15));
    }

    private ClientMissingCoreDataDetector CreateSut(DateOnly today)
    {
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new ClientMissingCoreDataDetector(_repo, NullLogger<ClientMissingCoreDataDetector>.Instance, tp);
    }

    private void StubStatuses(params ClientCoreDataStatus[] statuses)
    {
        _repo.GetActiveClientsWithMissingCoreDataAsync(Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(statuses.ToList());
    }

    [Test]
    public async Task DetectAsync_NoClientsWithGaps_ReturnsEmpty()
    {
        StubStatuses();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _repo.Received(1).GetActiveClientsWithMissingCoreDataAsync(
            new DateOnly(2026, 1, 15), ClientMissingCoreDataDetector.MaxFindingsPerTick, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_ClientWithCompleteCoreData_EmitsNothing()
    {
        StubStatuses(new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", true, true));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ClientWithoutActiveAddress_EmitsMediumAddressEvent()
    {
        var clientId = Guid.NewGuid();
        StubStatuses(new ClientCoreDataStatus(clientId, "Max", "Müller", false, true));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (ClientMissingCoreDataTriggerEvent)events[0];
        Assert.That(evt.MissingField, Is.EqualTo(ClientMissingCoreDataTriggerEvent.AddressField));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(evt.DedupKey, Is.EqualTo($"{clientId}:address"));
        Assert.That(evt.SummaryParams["name"], Is.EqualTo("Max Müller"));
    }

    [Test]
    public async Task DetectAsync_ClientWithoutEmailAndPhone_EmitsLowContactEvent()
    {
        var clientId = Guid.NewGuid();
        StubStatuses(new ClientCoreDataStatus(clientId, "Max", "Müller", true, false));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (ClientMissingCoreDataTriggerEvent)events[0];
        Assert.That(evt.MissingField, Is.EqualTo(ClientMissingCoreDataTriggerEvent.ContactField));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
        Assert.That(evt.DedupKey, Is.EqualTo($"{clientId}:contact"));
    }

    [Test]
    public async Task DetectAsync_ClientMissingBoth_EmitsTwoEventsWithDistinctDedupKeys()
    {
        var clientId = Guid.NewGuid();
        StubStatuses(new ClientCoreDataStatus(clientId, "Max", "Müller", false, false));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events.Select(e => e.DedupKey).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task DetectAsync_MoreFindingsThanCap_EmitsAtMostMaxFindingsPerTick()
    {
        var statuses = Enumerable.Range(0, ClientMissingCoreDataDetector.MaxFindingsPerTick)
            .Select(i => new ClientCoreDataStatus(Guid.NewGuid(), "Max", $"Müller{i}", false, false))
            .ToArray();
        StubStatuses(statuses);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(ClientMissingCoreDataDetector.MaxFindingsPerTick));
    }
}
