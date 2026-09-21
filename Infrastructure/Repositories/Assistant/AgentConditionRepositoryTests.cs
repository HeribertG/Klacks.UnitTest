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
using Klacks.Api.Domain.Services.Assistant;
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
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string OtherKind = AgentTriggerKinds.OpenOrder;

    /// <summary>
    /// A kind that does NOT declare RequiresGroupScope: its findings are about a client, not a group, so a
    /// null GroupId genuinely means "concerns the whole installation" and stays visible to every planner.
    /// Kept distinct from <see cref="Kind"/> because the two now behave differently on a null GroupId.
    /// </summary>
    private const string UngroupedByNatureKind = AgentTriggerKinds.TargetHoursDrift;

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

    /// <param name="groupId">The row's primary group. Also becomes its single agent_condition_groups row,
    /// because that is the pair detection produces and the scope reads now gate on the join table.</param>
    private static AgentCondition Condition(
        string triggerKind,
        string fingerprint,
        AgentConditionStatus status,
        DateTime detectedAtUtc,
        string severity = AgentTriggerSeverity.Low,
        Guid? groupId = null) =>
        MultiGroupCondition(
            triggerKind,
            fingerprint,
            status,
            detectedAtUtc,
            severity,
            groupId.HasValue ? new HashSet<Guid> { groupId.Value } : new HashSet<Guid>());

    /// <summary>
    /// A row that concerns SEVERAL groups, the shape a shift-borne finding has: GroupId keeps the smallest
    /// as the primary group, and every group gets its agent_condition_groups row.
    /// </summary>
    private static AgentCondition MultiGroupCondition(
        string triggerKind,
        string fingerprint,
        AgentConditionStatus status,
        DateTime detectedAtUtc,
        string severity,
        IReadOnlySet<Guid> groupIds)
    {
        var id = Guid.NewGuid();

        return new AgentCondition
        {
            Id = id,
            TriggerKind = triggerKind,
            Fingerprint = fingerprint,
            Severity = severity,
            Status = status,
            DetectedAtUtc = detectedAtUtc,
            LastSeenAtUtc = detectedAtUtc,
            GroupId = AgentConditionLedgerPolicy.PrimaryGroupIdFor(groupIds),
            Groups = groupIds
                .Select(groupId => new AgentConditionGroup { ConditionId = id, GroupId = groupId })
                .ToList(),
            PayloadJson = "{}"
        };
    }

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

    private static AgentConditionEvent ClaimEvent(Guid conditionId, DateTime atUtc) => new()
    {
        Id = Guid.NewGuid(),
        ConditionId = conditionId,
        EventType = AgentConditionStatus.Prepared.ToString(),
        AtUtc = atUtc,
        Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "seeded"
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
            .InsertAsync(condition, DetectionEvent(condition.Id, StartUtc), new HashSet<Guid>());

        inserted.ShouldNotBeNull();

        using var verify = CreateContext();
        (await verify.AgentConditions.CountAsync()).ShouldBe(1);
        var storedEvent = await verify.AgentConditionEvents.SingleAsync();
        storedEvent.ConditionId.ShouldBe(condition.Id);
        storedEvent.EventType.ShouldBe(AgentConditionStatus.Detected.ToString());
    }

    /// <summary>
    /// The group set is persisted in the SAME SaveChangesAsync as the condition and its detection event.
    /// A row whose groups arrived in a second write could be read by another instance's scoped query in
    /// between and be withheld from planners it belongs to.
    /// </summary>
    [Test]
    public async Task Insert_PersistsTheWholeGroupSet_NotOnlyThePrimaryGroup()
    {
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();

        using var context = CreateContext();
        var condition = Condition(Kind, "fp-insert-groups", AgentConditionStatus.Detected, StartUtc);
        condition.GroupId = firstGroupId;

        await new AgentConditionRepository(context).InsertAsync(
            condition,
            DetectionEvent(condition.Id, StartUtc),
            new HashSet<Guid> { firstGroupId, secondGroupId });

        using var verify = CreateContext();
        var stored = await verify.AgentConditionGroups
            .Where(g => g.ConditionId == condition.Id)
            .Select(g => g.GroupId)
            .ToListAsync();

        stored.ShouldBe(new[] { firstGroupId, secondGroupId }, ignoreOrder: true);
    }

    [Test]
    public async Task SyncGroups_AddsTheMissingGroups_AndDropsTheOnesNoLongerReported()
    {
        var keptGroupId = Guid.NewGuid();
        var droppedGroupId = Guid.NewGuid();
        var addedGroupId = Guid.NewGuid();

        using var context = CreateContext();
        var condition = MultiGroupCondition(
            Kind,
            "fp-sync",
            AgentConditionStatus.Reported,
            StartUtc,
            AgentTriggerSeverity.Low,
            new HashSet<Guid> { keptGroupId, droppedGroupId });
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        var changed = await new AgentConditionRepository(context)
            .SyncGroupsAsync(condition.Id, new HashSet<Guid> { keptGroupId, addedGroupId });

        changed.ShouldBeTrue();

        using var verify = CreateContext();
        var stored = await verify.AgentConditionGroups
            .Where(g => g.ConditionId == condition.Id)
            .Select(g => g.GroupId)
            .ToListAsync();

        stored.ShouldBe(new[] { keptGroupId, addedGroupId }, ignoreOrder: true);
    }

    /// <summary>
    /// A tick re-observes every open row - thousands of them - and virtually none change groups, so the
    /// unchanged case must cost no write at all. The false return is what the ledger service's own test
    /// asserts that rule through.
    /// </summary>
    [Test]
    public async Task SyncGroups_WithAnUnchangedSet_ReportsNoWrite()
    {
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        var groupIds = new HashSet<Guid> { firstGroupId, secondGroupId };

        using var context = CreateContext();
        var condition = MultiGroupCondition(
            Kind, "fp-sync-noop", AgentConditionStatus.Reported, StartUtc, AgentTriggerSeverity.Low, groupIds);
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        var changed = await new AgentConditionRepository(context).SyncGroupsAsync(condition.Id, groupIds);

        changed.ShouldBeFalse();

        using var verify = CreateContext();
        (await verify.AgentConditionGroups.CountAsync(g => g.ConditionId == condition.Id)).ShouldBe(2);
    }

    /// <summary>
    /// THE MULTI-GROUP BUG ITSELF. A shift belongs to several groups at once, so a finding about it
    /// concerns all of them; AgentCondition.GroupId can name only one, and while the scoped reads gated on
    /// that column the planners of every other group could not find the finding at all - not through
    /// list_open_findings, not through the chat context block, not through the digest - although the live
    /// push had correctly reached them. Each group is deliberately a DIFFERENT Nested Set root here, so a
    /// fix that merely widened the subtree comparison would not pass.
    /// </summary>
    [Test]
    public async Task GetOpenForScope_MultiGroupFinding_IsVisibleToAPlannerOfEveryOneOfItsGroups()
    {
        using var context = CreateContext();
        var firstRoot = Guid.NewGuid();
        var secondRoot = Guid.NewGuid();
        var secondChild = Guid.NewGuid();
        var foreignRoot = Guid.NewGuid();
        context.Group.AddRange(
            GroupRow(firstRoot, root: firstRoot, parent: null),
            GroupRow(secondRoot, root: secondRoot, parent: null),
            GroupRow(secondChild, root: secondRoot, parent: secondRoot),
            GroupRow(foreignRoot, root: foreignRoot, parent: null));

        // The second group is a CHILD, so its planner is only reachable through the root fallback - the
        // join table must be resolved the same way the single-group column was.
        var multiGroup = MultiGroupCondition(
            Kind,
            "fp-multi-group",
            AgentConditionStatus.Reported,
            StartUtc,
            AgentTriggerSeverity.High,
            new HashSet<Guid> { firstRoot, secondChild });
        context.AgentConditions.Add(multiGroup);
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);
        var firstPlanner = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid> { firstRoot }, take: 20);
        var secondPlanner = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid> { secondRoot }, take: 20);
        var foreignPlanner = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid> { foreignRoot }, take: 20);

        firstPlanner.Select(c => c.Id).ShouldContain(multiGroup.Id);
        secondPlanner.Select(c => c.Id).ShouldContain(
            multiGroup.Id,
            "The planner of the second group must find the finding too - that is the whole point of the join table.");
        foreignPlanner.Select(c => c.Id).ShouldNotContain(multiGroup.Id);

        (await repository.CountOpenForScopeAsync(isUnrestricted: false, visibleRootIds: new HashSet<Guid> { secondRoot }))
            .ShouldBe(1);
    }

    /// <summary>
    /// Same finding, same second group, through the other two entry points on the shared scope fragment:
    /// the per-turn chat context block and the single-row read Etappe 4e delegation resolves through. Only
    /// checking GetOpenForScopeAsync would let either of them keep the old narrowing.
    /// </summary>
    [Test]
    public async Task GetTopForContextAndById_MultiGroupFinding_AreVisibleToTheSecondGroupsPlanner()
    {
        using var context = CreateContext();
        var firstRoot = Guid.NewGuid();
        var secondRoot = Guid.NewGuid();
        context.Group.AddRange(
            GroupRow(firstRoot, root: firstRoot, parent: null),
            GroupRow(secondRoot, root: secondRoot, parent: null));

        var multiGroup = MultiGroupCondition(
            Kind,
            "fp-multi-group-context",
            AgentConditionStatus.Reported,
            StartUtc,
            AgentTriggerSeverity.High,
            new HashSet<Guid> { firstRoot, secondRoot });
        context.AgentConditions.Add(multiGroup);
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);
        var scope = new HashSet<Guid> { secondRoot };

        var contextBlock = await repository.GetTopForContextAsync(
            isUnrestricted: false, visibleRootIds: scope, preferredGroupId: null, take: 20);
        var single = await repository.GetOpenForScopeByIdAsync(multiGroup.Id, isUnrestricted: false, scope);

        contextBlock.Select(c => c.Id).ShouldContain(multiGroup.Id);
        single.ShouldNotBeNull();
    }

    /// <summary>
    /// A row whose primary GroupId is set but whose join rows are missing - a row written before the
    /// backfill, or one whose backfill did not run - must fall back to Admins, exactly where a row of a
    /// group-scoped kind with no group at all already falls. The one thing it must not do is become
    /// visible to every planner.
    /// </summary>
    [Test]
    public async Task GetOpenForScope_NonAdmin_DoesNotSeeARowWhoseGroupSetIsMissing()
    {
        using var context = CreateContext();
        var ownRoot = Guid.NewGuid();
        context.Group.Add(GroupRow(ownRoot, root: ownRoot, parent: null));

        var withoutJoinRows = Condition(
            Kind, "fp-no-join-rows", AgentConditionStatus.Reported, StartUtc, groupId: ownRoot);
        withoutJoinRows.Groups.Clear();
        context.AgentConditions.Add(withoutJoinRows);
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);
        var planner = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid> { ownRoot }, take: 20);
        var admin = await repository.GetOpenForScopeAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 20);

        planner.Select(c => c.Id).ShouldNotContain(withoutJoinRows.Id);
        admin.Select(c => c.Id).ShouldContain(withoutJoinRows.Id);
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
        var ungatedFinding = Condition(UngroupedByNatureKind, "fp-ungated", AgentConditionStatus.Reported, StartUtc, groupId: null);
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

    /// <summary>
    /// The read-path half of the group-scope leak the live push closed first. A row of a
    /// RequiresGroupScope kind whose GroupId is null does not mean "concerns everybody"; it means the group
    /// of a group-owned entity could not be determined - the 52 empty_container/uncut_fullday_shift rows
    /// that predate the push fix are exactly this, and they keep their null GroupId for as long as they
    /// stay open because a re-detection only touches LastSeenAtUtc. A populated but unrelated scope is
    /// tested alongside the empty one on purpose: the naive fix leaves the GroupId-null branch untouched,
    /// which passes an empty-scope test and still leaks to every planner who has any visibility row at all.
    /// </summary>
    [Test]
    public async Task GetOpenForScope_NonAdmin_DoesNotSeeAGroupScopedKindWhoseGroupIsUnknown()
    {
        using var context = CreateContext();
        var ownRoot = Guid.NewGuid();
        context.Group.Add(GroupRow(ownRoot, root: ownRoot, parent: null));

        var groupScopedUngrouped = Condition(Kind, "fp-scoped-ungrouped", AgentConditionStatus.Reported, StartUtc, groupId: null);
        var globalUngrouped = Condition(UngroupedByNatureKind, "fp-global-ungrouped", AgentConditionStatus.Reported, StartUtc, groupId: null);
        context.AgentConditions.AddRange(groupScopedUngrouped, globalUngrouped);
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);
        var withScope = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid> { ownRoot }, take: 20);
        var withoutScope = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid>(), take: 20);

        withScope.Select(c => c.Id).ShouldNotContain(groupScopedUngrouped.Id);
        withScope.Select(c => c.Id).ShouldContain(globalUngrouped.Id);
        withoutScope.Select(c => c.Id).ShouldNotContain(groupScopedUngrouped.Id);
        withoutScope.Select(c => c.Id).ShouldContain(globalUngrouped.Id);
    }

    [Test]
    public async Task CountOpenForScope_NonAdmin_ExcludesAGroupScopedKindWhoseGroupIsUnknown()
    {
        using var context = CreateContext();
        context.AgentConditions.AddRange(
            Condition(Kind, "fp-scoped-ungrouped", AgentConditionStatus.Reported, StartUtc, groupId: null),
            Condition(OtherKind, "fp-scoped-ungrouped-2", AgentConditionStatus.Detected, StartUtc, groupId: null),
            Condition(UngroupedByNatureKind, "fp-global-ungrouped", AgentConditionStatus.Reported, StartUtc, groupId: null));
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);

        (await repository.CountOpenForScopeAsync(isUnrestricted: false, visibleRootIds: new HashSet<Guid>())).ShouldBe(1);
        (await repository.CountOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>())).ShouldBe(3);
    }

    [Test]
    public async Task GetOpenForScope_Admin_StillSeesAGroupScopedKindWhoseGroupIsUnknown()
    {
        // The withheld rows are not dropped, they fall back to the audience that is unrestricted anyway -
        // the same landing place AgentTriggerService.ResolvePlannerAudienceAsync gives them on the push path.
        using var context = CreateContext();
        var groupScopedUngrouped = Condition(Kind, "fp-scoped-ungrouped", AgentConditionStatus.Reported, StartUtc, groupId: null);
        context.AgentConditions.Add(groupScopedUngrouped);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 20);

        result.Select(c => c.Id).ShouldContain(groupScopedUngrouped.Id);
    }

    [Test]
    public async Task GetTopForContext_NonAdmin_DoesNotSeeAGroupScopedKindWhoseGroupIsUnknown()
    {
        // GetTopForContextAsync is the second entry point on the shared query fragment (the [OPEN_FINDINGS]
        // chat block), so the withholding has to hold there too, not only in list_open_findings.
        using var context = CreateContext();
        var ownRoot = Guid.NewGuid();
        context.Group.Add(GroupRow(ownRoot, root: ownRoot, parent: null));

        var groupScopedUngrouped = Condition(
            Kind, "fp-scoped-ungrouped", AgentConditionStatus.Reported, StartUtc, AgentTriggerSeverity.High);
        var globalUngrouped = Condition(
            UngroupedByNatureKind, "fp-global-ungrouped", AgentConditionStatus.Reported, StartUtc, AgentTriggerSeverity.High);
        context.AgentConditions.AddRange(groupScopedUngrouped, globalUngrouped);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: false,
            visibleRootIds: new HashSet<Guid> { ownRoot },
            preferredGroupId: null,
            take: 20);

        result.Select(c => c.Id).ShouldNotContain(groupScopedUngrouped.Id);
        result.Select(c => c.Id).ShouldContain(globalUngrouped.Id);
    }

    [Test]
    public async Task GetOpenForScope_NonAdmin_WithNoGroupVisibilityRows_SeesOnlyUngatedRows()
    {
        using var context = CreateContext();
        var someRoot = Guid.NewGuid();
        context.Group.Add(GroupRow(someRoot, root: someRoot, parent: null));

        var gatedFinding = Condition(Kind, "fp-gated", AgentConditionStatus.Reported, StartUtc, groupId: someRoot);
        var ungatedFinding = Condition(UngroupedByNatureKind, "fp-ungated", AgentConditionStatus.Reported, StartUtc, groupId: null);
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

    /// <summary>
    /// The single-row counterpart of <see cref="GetOpenForScope_NonAdmin_SeesOwnSubtreeAndUngatedRows_NotAForeignRoot"/>,
    /// exercising the id filter Etappe 4e delegation relies on to answer "not found" for a condition
    /// outside the caller's own scope instead of confirming it exists.
    /// </summary>
    [Test]
    public async Task GetOpenForScopeById_NonAdmin_ReturnsOwnRootRowButNullForAForeignRoot()
    {
        using var context = CreateContext();
        var ownRoot = Guid.NewGuid();
        var foreignRoot = Guid.NewGuid();
        context.Group.AddRange(
            GroupRow(ownRoot, root: ownRoot, parent: null),
            GroupRow(foreignRoot, root: foreignRoot, parent: null));

        var ownFinding = Condition(Kind, "fp-own-root-single", AgentConditionStatus.Reported, StartUtc, groupId: ownRoot);
        var foreignFinding = Condition(Kind, "fp-foreign-single", AgentConditionStatus.Reported, StartUtc, groupId: foreignRoot);
        context.AgentConditions.AddRange(ownFinding, foreignFinding);
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);
        var visibleRootIds = new HashSet<Guid> { ownRoot };
        var ownResult = await repository.GetOpenForScopeByIdAsync(ownFinding.Id, isUnrestricted: false, visibleRootIds);
        var foreignResult = await repository.GetOpenForScopeByIdAsync(foreignFinding.Id, isUnrestricted: false, visibleRootIds);

        ownResult.ShouldNotBeNull();
        ownResult!.Id.ShouldBe(ownFinding.Id);
        foreignResult.ShouldBeNull();
    }

    [Test]
    public async Task GetOpenForScopeById_UnknownId_ReturnsNull()
    {
        using var context = CreateContext();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeByIdAsync(Guid.NewGuid(), isUnrestricted: true, new HashSet<Guid>());

        result.ShouldBeNull();
    }

    [TestCase(AgentConditionStatus.Executed)]
    [TestCase(AgentConditionStatus.Rejected)]
    [TestCase(AgentConditionStatus.Resolved)]
    public async Task GetOpenForScopeById_TerminalNonEscalatedStatus_ReturnsNull(AgentConditionStatus status)
    {
        using var context = CreateContext();
        var condition = Condition(Kind, "fp-terminal-single", status, StartUtc);
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context)
            .GetOpenForScopeByIdAsync(condition.Id, isUnrestricted: true, new HashSet<Guid>());

        result.ShouldBeNull();
    }

    /// <summary>
    /// The budget count is scoped to ONE group, because that is the scope its limit is configured in.
    /// A claim of another group must never appear in this number: pooling is what let a busy group spend
    /// a quiet one's budget.
    ///
    /// Proven here against the EF InMemory provider, which pins the FILTERING contract but not the SQL
    /// the filter turns into. That the null branch really has to be written as a separate IS NULL query
    /// rather than one GroupId == groupId comparison is a claim about Npgsql's translation, and only the
    /// PostgreSQL-backed test of the same name in Klacks.IntegrationTest can speak to it.
    /// </summary>
    [Test]
    public async Task CountActionClaims_CountsOnlyTheClaimsOfTheGroupItIsAskedAbout()
    {
        var busyGroupId = Guid.NewGuid();
        var quietGroupId = Guid.NewGuid();

        using var context = CreateContext();
        var busy = Condition(Kind, "fp-claims-busy", AgentConditionStatus.Prepared, StartUtc, groupId: busyGroupId);
        var quiet = Condition(Kind, "fp-claims-quiet", AgentConditionStatus.Prepared, StartUtc, groupId: quietGroupId);
        context.AgentConditions.AddRange(busy, quiet);
        context.AgentConditionEvents.AddRange(
            ClaimEvent(busy.Id, StartUtc.AddMinutes(10)),
            ClaimEvent(busy.Id, StartUtc.AddMinutes(20)),
            ClaimEvent(quiet.Id, StartUtc.AddMinutes(30)));
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);

        (await repository.CountActionClaimsAsync(Kind, busyGroupId, StartUtc)).ShouldBe(2);
        (await repository.CountActionClaimsAsync(Kind, quietGroupId, StartUtc)).ShouldBe(
            1,
            "The busy group's two claims belong to the busy group's budget alone.");
    }

    /// <summary>
    /// A null groupId is the installation-wide bucket - the rows that carry no group at all - and NOT
    /// "any group". Both mistakes are silent in production: counting every group would block the
    /// installation-wide kinds far too early, counting nothing would let them believe their budget
    /// untouched forever.
    /// </summary>
    [Test]
    public async Task CountActionClaims_WithoutAGroup_CountsTheGroupLessRows_NeitherAllOfThemNorNone()
    {
        var groupId = Guid.NewGuid();

        using var context = CreateContext();
        var installationWide = Condition(
            UngroupedByNatureKind, "fp-claims-global", AgentConditionStatus.Prepared, StartUtc);
        var grouped = Condition(
            UngroupedByNatureKind, "fp-claims-grouped", AgentConditionStatus.Prepared, StartUtc, groupId: groupId);
        context.AgentConditions.AddRange(installationWide, grouped);
        context.AgentConditionEvents.AddRange(
            ClaimEvent(installationWide.Id, StartUtc.AddMinutes(10)),
            ClaimEvent(grouped.Id, StartUtc.AddMinutes(20)),
            ClaimEvent(grouped.Id, StartUtc.AddMinutes(30)));
        await context.SaveChangesAsync();

        var count = await new AgentConditionRepository(context)
            .CountActionClaimsAsync(UngroupedByNatureKind, groupId: null, StartUtc);

        count.ShouldBe(
            1,
            "One claim carries no group. Zero would mean the null comparison matched nothing, three "
            + "would mean the group filter was skipped altogether.");
    }

    [Test]
    public async Task CountActionClaims_IgnoresOtherKinds_EventsWithoutTheClaimMarker_AndEventsBeforeTheWindow()
    {
        var groupId = Guid.NewGuid();

        using var context = CreateContext();
        var mine = Condition(Kind, "fp-claims-mine", AgentConditionStatus.Prepared, StartUtc, groupId: groupId);
        var foreignKind = Condition(
            OtherKind, "fp-claims-foreign", AgentConditionStatus.Prepared, StartUtc, groupId: groupId);
        context.AgentConditions.AddRange(mine, foreignKind);
        context.AgentConditionEvents.AddRange(
            ClaimEvent(mine.Id, StartUtc.AddMinutes(10)),
            ClaimEvent(mine.Id, StartUtc.AddMinutes(-10)),
            DetectionEvent(mine.Id, StartUtc.AddMinutes(20)),
            ClaimEvent(foreignKind.Id, StartUtc.AddMinutes(30)));
        await context.SaveChangesAsync();

        var count = await new AgentConditionRepository(context)
            .CountActionClaimsAsync(Kind, groupId, StartUtc);

        count.ShouldBe(1);
    }
}
