// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ScenarioPendingDetector — the reporting window (48 h .. 30 days) is handed to the
/// repository instead of being filtered in memory, severity follows the age, the group name is carried,
/// and a draft an automatic next-period run already reported as blocked is not reported again.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ScenarioPendingDetectorTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private IAnalyseScenarioRepository _repo = null!;
    private IAgentConditionRepository _conditions = null!;
    private ScenarioPendingDetector _sut = null!;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IAnalyseScenarioRepository>();
        _conditions = Substitute.For<IAgentConditionRepository>();
        _conditions.GetOpenByKindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());
        _sut = new ScenarioPendingDetector(
            _repo, _conditions, new FixedTimeProvider(NowUtc), NullLogger<ScenarioPendingDetector>.Instance);
    }

    private static AnalyseScenario MakeScenario(int hoursOld) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        Token = Guid.NewGuid(),
        Status = AnalyseScenarioStatus.Active,
        CreateTime = NowUtc.AddHours(-hoursOld)
    };

    private void RepositoryReturns(params AnalyseScenario[] scenarios) =>
        _repo.GetActiveCreatedBetweenAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(scenarios.ToList());

    private static AgentCondition LedgerRow(IAgentTriggerEvent triggerEvent) => new()
    {
        TriggerKind = triggerEvent.Kind,
        Fingerprint = triggerEvent.DedupKey,
        PayloadJson = JsonSerializer.Serialize(triggerEvent.Payload)
    };

    [Test]
    public async Task DetectAsync_NoScenarios_ReturnsEmpty()
    {
        RepositoryReturns();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AsksTheRepositoryForActiveScenariosBetween30DaysAnd48HoursOld()
    {
        RepositoryReturns();

        await _sut.DetectAsync();

        await _repo.Received(1).GetActiveCreatedBetweenAsync(
            NowUtc.AddDays(-ScenarioPendingDetector.MaximumPendingDays),
            NowUtc.AddHours(-ScenarioPendingDetector.MinimumPendingHours),
            Arg.Any<CancellationToken>());
        Assert.That(ScenarioPendingDetector.MaximumPendingDays, Is.EqualTo(30));
        Assert.That(ScenarioPendingDetector.MinimumPendingHours, Is.EqualTo(48));
    }

    [Test]
    public async Task DetectAsync_ActiveAndOlderThan48h_EmitsEvent()
    {
        RepositoryReturns(MakeScenario(hoursOld: 72));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var pending = events.Single() as ScenarioPendingTriggerEvent;
        Assert.That(pending!.HoursPending, Is.EqualTo(72));
        Assert.That(pending.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_ScenarioOfAGroup_IsDetectedWithItsGroupName()
    {
        var group = new Klacks.Api.Domain.Models.Associations.Group { Id = Guid.NewGuid(), Name = "Winterthur" };
        var scenario = MakeScenario(hoursOld: 72);
        scenario.GroupId = group.Id;
        scenario.Group = group;
        RepositoryReturns(scenario);

        var events = await _sut.DetectAsync();

        var pending = events.Single() as ScenarioPendingTriggerEvent;
        Assert.That(pending!.GroupId, Is.EqualTo(group.Id));
        Assert.That(pending.GroupName, Is.EqualTo("Winterthur"));
    }

    [Test]
    public async Task DetectAsync_ScenarioAlreadyReportedAsBlockedAutoCommit_IsNotReportedAgain()
    {
        var blocked = MakeScenario(hoursOld: 72);
        var other = MakeScenario(hoursOld: 72);
        RepositoryReturns(blocked, other);
        var blockedEvent = new NextPeriodAutoCommitBlockedTriggerEvent(
            Guid.NewGuid(), "Bern", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
            blocked.Id, 0, NextPeriodAutoCommitBlockReason.HarmonizationSkipped);
        _conditions.GetOpenByKindAsync(AgentTriggerKinds.NextPeriodSchedulingDue, Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { LedgerRow(blockedEvent) });

        var events = await _sut.DetectAsync();

        var reported = events.Cast<ScenarioPendingTriggerEvent>().Select(e => e.ScenarioId).ToList();
        Assert.That(reported, Is.EqualTo(new[] { other.Id }));
    }

    [Test]
    public async Task DetectAsync_LedgerRowsWithoutBlockReasonOrUnreadable_DoNotSuppressAnything()
    {
        var scenario = MakeScenario(hoursOld: 72);
        RepositoryReturns(scenario);
        _conditions.GetOpenByKindAsync(AgentTriggerKinds.NextPeriodSchedulingDue, Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>
            {
                new() { PayloadJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["scenarioId"] = scenario.Id }) },
                new() { PayloadJson = "not json" },
                new() { PayloadJson = "{\"blockReason\":\"Timeout\",\"scenarioId\":null}" }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
    }
}
