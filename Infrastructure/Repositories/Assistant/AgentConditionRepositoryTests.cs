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

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
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

    private static AgentCondition Condition(
        string triggerKind,
        string fingerprint,
        AgentConditionStatus status,
        DateTime detectedAtUtc,
        string severity = AgentTriggerSeverity.Low,
        Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = triggerKind,
        Fingerprint = fingerprint,
        Severity = severity,
        Status = status,
        DetectedAtUtc = detectedAtUtc,
        LastSeenAtUtc = detectedAtUtc,
        GroupId = groupId,
        PayloadJson = "{}"
    };

    private static Group GroupRow(Guid id, Guid? root, Guid? parent) => new()
    {
        Id = id,
        Name = id.ToString(),
        ValidFrom = StartUtc,
        Root = root,
        Parent = parent
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

    [Test]
    public async Task GetOpenForScope_WithNoMatchingRows_ReturnsEmpty()
    {
        using var context = CreateContext();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 20);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetOpenForScope_SortsBySeverityHighToLow_ThenByAgeOldestFirst()
    {
        using var context = CreateContext();
        var medium = Condition(Kind, "fp-medium", AgentConditionStatus.Reported, StartUtc.AddHours(1), AgentTriggerSeverity.Medium);
        var highNewer = Condition(Kind, "fp-high-newer", AgentConditionStatus.Reported, StartUtc.AddHours(3), AgentTriggerSeverity.High);
        var highOlder = Condition(Kind, "fp-high-older", AgentConditionStatus.Reported, StartUtc, AgentTriggerSeverity.High);
        var low = Condition(Kind, "fp-low", AgentConditionStatus.Detected, StartUtc.AddHours(2), AgentTriggerSeverity.Low);
        context.AgentConditions.AddRange(medium, highNewer, highOlder, low);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 20);

        result.Select(c => c.Id).ShouldBe(new[] { highOlder.Id, highNewer.Id, medium.Id, low.Id });
    }

    [Test]
    public async Task GetOpenForScope_EscalatedRowAndAFreshReArmRow_BothAppear()
    {
        // Regression against the AgentConditionStateMachine.OpenStatuses trap (Etappe 3b): Escalated is
        // terminal for the partial unique index, so a re-arm can legitimately open a fresh row for the
        // same fingerprint while the Escalated row still exists. list_open_findings must show both -
        // "escalated after N attempts, and re-detected since" - not silently drop the escalated one.
        using var context = CreateContext();
        var escalated = Condition(Kind, "fp-escalated", AgentConditionStatus.Escalated, StartUtc, AgentTriggerSeverity.High);
        escalated.AttemptCount = 3;
        escalated.EscalatedAtUtc = StartUtc.AddHours(1);
        var reArmed = Condition(Kind, "fp-escalated", AgentConditionStatus.Detected, StartUtc.AddHours(2), AgentTriggerSeverity.High);
        context.AgentConditions.AddRange(escalated, reArmed);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 20);

        result.Select(c => c.Id).ShouldBe(new[] { escalated.Id, reArmed.Id });
    }

    [TestCase(AgentConditionStatus.Executed)]
    [TestCase(AgentConditionStatus.Rejected)]
    [TestCase(AgentConditionStatus.Resolved)]
    public async Task GetOpenForScope_ExcludesTerminalStatusesOtherThanEscalated(AgentConditionStatus status)
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(Kind, "fp-terminal", status, StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 20);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetOpenForScope_NonAdmin_SeesOwnSubtreeAndUngatedRows_NotAForeignRoot()
    {
        using var context = CreateContext();
        var ownRoot = Guid.NewGuid();
        var ownChild = Guid.NewGuid();
        var foreignRoot = Guid.NewGuid();
        context.Group.AddRange(
            GroupRow(ownRoot, root: ownRoot, parent: null),
            GroupRow(ownChild, root: ownRoot, parent: ownRoot),
            GroupRow(foreignRoot, root: foreignRoot, parent: null));

        var ownRootFinding = Condition(Kind, "fp-own-root", AgentConditionStatus.Reported, StartUtc, groupId: ownRoot);
        var ownChildFinding = Condition(Kind, "fp-own-child", AgentConditionStatus.Reported, StartUtc, groupId: ownChild);
        var foreignFinding = Condition(Kind, "fp-foreign", AgentConditionStatus.Reported, StartUtc, groupId: foreignRoot);
        var ungatedFinding = Condition(Kind, "fp-ungated", AgentConditionStatus.Reported, StartUtc, groupId: null);
        context.AgentConditions.AddRange(ownRootFinding, ownChildFinding, foreignFinding, ungatedFinding);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeAsync(isUnrestricted: false, visibleRootIds: new HashSet<Guid> { ownRoot }, take: 20);

        var resultIds = result.Select(c => c.Id).ToHashSet();
        resultIds.ShouldContain(ownRootFinding.Id);
        resultIds.ShouldContain(ownChildFinding.Id);
        resultIds.ShouldContain(ungatedFinding.Id);
        resultIds.ShouldNotContain(foreignFinding.Id);
    }

    [Test]
    public async Task GetOpenForScope_NonAdmin_WithNoGroupVisibilityRows_SeesOnlyUngatedRows()
    {
        using var context = CreateContext();
        var someRoot = Guid.NewGuid();
        context.Group.Add(GroupRow(someRoot, root: someRoot, parent: null));

        var gatedFinding = Condition(Kind, "fp-gated", AgentConditionStatus.Reported, StartUtc, groupId: someRoot);
        var ungatedFinding = Condition(Kind, "fp-ungated", AgentConditionStatus.Reported, StartUtc, groupId: null);
        context.AgentConditions.AddRange(gatedFinding, ungatedFinding);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeAsync(isUnrestricted: false, visibleRootIds: new HashSet<Guid>(), take: 20);

        var resultIds = result.Select(c => c.Id).ToHashSet();
        resultIds.ShouldContain(ungatedFinding.Id);
        resultIds.ShouldNotContain(gatedFinding.Id);
    }

    [Test]
    public async Task GetOpenForScope_TakeCapsTheReturnedRows_ButCountIgnoresTheCap()
    {
        using var context = CreateContext();
        context.AgentConditions.AddRange(
            Condition(Kind, "fp-1", AgentConditionStatus.Detected, StartUtc),
            Condition(Kind, "fp-2", AgentConditionStatus.Detected, StartUtc.AddMinutes(1)),
            Condition(Kind, "fp-3", AgentConditionStatus.Detected, StartUtc.AddMinutes(2)),
            Condition(Kind, "fp-4", AgentConditionStatus.Detected, StartUtc.AddMinutes(3)),
            Condition(Kind, "fp-5", AgentConditionStatus.Detected, StartUtc.AddMinutes(4)));
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);
        var capped = await repository.GetOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 2);
        var total = await repository.CountOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>());

        capped.Count.ShouldBe(2);
        total.ShouldBe(5);
    }
}
