// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentConditionRepository's query and insert surface against a shared in-memory
/// DataBaseContext, mirroring the neighbouring repository tests: which rows count as open, that the
/// fingerprint lookup deliberately ignores TriggerKind, and that a new condition and its first audit
/// event are persisted together.
///
/// Not covered here, by the provider's limits rather than by choice: TryTransitionAsync and
/// TouchLastSeenAsync use ExecuteUpdateAsync, which the EF in-memory provider does not support, and the
/// null return of InsertAsync depends on the partial unique index, which the in-memory provider ignores.
/// Both live in Klacks.IntegrationTest/Infrastructure/Repositories/AgentConditionRepositoryCasTests.cs
/// against a real PostgreSQL database.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class AgentConditionRepositoryTests
{
    private const string Kind = "empty_container";
    private const string OtherKind = "open_order";

    private static readonly DateTime StartUtc = new(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static AgentCondition Condition(string triggerKind, string fingerprint, AgentConditionStatus status, DateTime detectedAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = triggerKind,
        Fingerprint = fingerprint,
        Severity = "low",
        Status = status,
        DetectedAtUtc = detectedAtUtc,
        LastSeenAtUtc = detectedAtUtc,
        PayloadJson = "{}"
    };

    private static AgentConditionEvent DetectionEvent(Guid conditionId, DateTime atUtc) => new()
    {
        Id = Guid.NewGuid(),
        ConditionId = conditionId,
        EventType = AgentConditionStatus.Detected.ToString(),
        AtUtc = atUtc
    };

    [TestCase(AgentConditionStatus.Detected)]
    [TestCase(AgentConditionStatus.Reported)]
    [TestCase(AgentConditionStatus.Prepared)]
    public async Task FindOpenByFingerprint_ReturnsRowsInAnOpenStatus(AgentConditionStatus status)
    {
        using var context = CreateContext();
        var condition = Condition(Kind, "fp-open", status, StartUtc);
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        var found = await new AgentConditionRepository(context).FindOpenByFingerprintAsync("fp-open");

        found.ShouldNotBeNull();
        found.Id.ShouldBe(condition.Id);
    }

    [TestCase(AgentConditionStatus.Executed)]
    [TestCase(AgentConditionStatus.Rejected)]
    [TestCase(AgentConditionStatus.Resolved)]
    [TestCase(AgentConditionStatus.Escalated)]
    public async Task FindOpenByFingerprint_IgnoresRowsInATerminalStatus(AgentConditionStatus status)
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(Kind, "fp-terminal", status, StartUtc));
        await context.SaveChangesAsync();

        var found = await new AgentConditionRepository(context).FindOpenByFingerprintAsync("fp-terminal");

        found.ShouldBeNull();
    }

    [Test]
    public async Task FindOpenByFingerprint_DoesNotFilterByKind_TheUniqueIndexDoesNotEither()
    {
        using var context = CreateContext();
        var condition = Condition(OtherKind, "fp-crosskind", AgentConditionStatus.Detected, StartUtc);
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        var found = await new AgentConditionRepository(context).FindOpenByFingerprintAsync("fp-crosskind");

        found.ShouldNotBeNull();
        found.TriggerKind.ShouldBe(OtherKind);
    }

    [Test]
    public async Task GetOpenByKind_ReturnsOpenRowsOfThatKindOnly_OldestDetectionFirst()
    {
        using var context = CreateContext();
        var newer = Condition(Kind, "fp-newer", AgentConditionStatus.Reported, StartUtc.AddHours(2));
        var older = Condition(Kind, "fp-older", AgentConditionStatus.Detected, StartUtc);
        context.AgentConditions.AddRange(
            newer,
            older,
            Condition(Kind, "fp-closed", AgentConditionStatus.Resolved, StartUtc),
            Condition(OtherKind, "fp-foreign", AgentConditionStatus.Detected, StartUtc));
        await context.SaveChangesAsync();

        var open = await new AgentConditionRepository(context).GetOpenByKindAsync(Kind);

        open.Select(c => c.Id).ShouldBe(new[] { older.Id, newer.Id });
    }

    [Test]
    public async Task Insert_PersistsTheConditionAndItsDetectionEvent()
    {
        using var context = CreateContext();
        var condition = Condition(Kind, "fp-insert", AgentConditionStatus.Detected, StartUtc);

        var inserted = await new AgentConditionRepository(context)
            .InsertAsync(condition, DetectionEvent(condition.Id, StartUtc));

        inserted.ShouldNotBeNull();

        using var verify = CreateContext();
        (await verify.AgentConditions.CountAsync()).ShouldBe(1);
        var storedEvent = await verify.AgentConditionEvents.SingleAsync();
        storedEvent.ConditionId.ShouldBe(condition.Id);
        storedEvent.EventType.ShouldBe(AgentConditionStatus.Detected.ToString());
    }

    [Test]
    public async Task InsertEvent_AppendsAStandaloneAuditRow()
    {
        using var context = CreateContext();
        var condition = Condition(Kind, "fp-event", AgentConditionStatus.Reported, StartUtc);
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        await new AgentConditionRepository(context).InsertEventAsync(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = condition.Id,
            EventType = "AttemptFailed",
            AtUtc = StartUtc,
            Detail = "optimizer busy"
        });

        using var verify = CreateContext();
        var storedEvent = await verify.AgentConditionEvents.SingleAsync();
        storedEvent.EventType.ShouldBe("AttemptFailed");
        storedEvent.Detail.ShouldBe("optimizer busy");
    }
}
