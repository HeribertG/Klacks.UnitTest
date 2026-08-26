// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentConditionRepository.GetExecutedForEntitiesAsync, the read behind the service grid's
/// "Klacksy handled this one" marker. Covers: an Executed row IS returned (the regression guard against
/// somebody rebuilding this on ScopedPlannerRelevantQuery, whose status filter excludes Executed and
/// would make every result silently empty), open and other terminal statuses are NOT returned, unknown
/// and empty id sets return nothing, rows without an EntityId are never matched, the group scope is
/// enforced in both directions for a restricted planner, a group-scoped kind with no GroupId stays with
/// admins, and several Executed rows on one entity come back newest handling first.
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
public class AgentConditionRepositoryGetExecutedForEntitiesTests
{
    private static readonly DateTime StartUtc = new(2026, 8, 26, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid VisibleRootId = Guid.NewGuid();
    private static readonly Guid ForeignRootId = Guid.NewGuid();

    private static readonly IReadOnlySet<Guid> NoRootIds = new HashSet<Guid>();

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
        string kind,
        AgentConditionStatus status,
        Guid? entityId,
        DateTime? handledAtUtc = null,
        Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = kind,
        Fingerprint = $"{kind}:{Guid.NewGuid()}",
        Severity = AgentTriggerSeverity.High,
        Status = status,
        EntityId = entityId,
        GroupId = groupId,
        HandledAtUtc = handledAtUtc,
        DetectedAtUtc = StartUtc,
        LastSeenAtUtc = StartUtc,
        PayloadJson = "{}"
    };

    [Test]
    public async Task ReturnsExecutedRow_TheGuardAgainstReusingScopedPlannerRelevantQuery()
    {
        // REGRESSION GUARD: ScopedPlannerRelevantQuery filters on
        // AgentConditionPlannerRelevantStatuses.Values, which does NOT contain Executed. Anybody who
        // rebuilds this read on that method gets an always-empty result that still looks correct, so this
        // test asserts a non-empty one. Status is the only variable here on purpose - unrestricted with an
        // empty root set - so a failure can only mean the status filter, never the group scope.
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, StartUtc.AddHours(1)));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.Count.ShouldBe(1);
        result[0].EntityId.ShouldBe(entityId);
        result[0].HandledAtUtc.ShouldBe(StartUtc.AddHours(1));
        result[0].TriggerKind.ShouldBe(AgentTriggerKinds.EmptyContainer);
    }

    [Test]
    public async Task ExcludesOpenRows()
    {
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        foreach (var status in AgentConditionPlannerRelevantStatuses.Values)
        {
            context.AgentConditions.Add(Condition(AgentTriggerKinds.EmptyContainer, status, entityId));
        }
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ExcludesOtherTerminalStatuses()
    {
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Rejected, entityId, StartUtc));
        context.AgentConditions.Add(Condition(AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Resolved, entityId, StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownEntityIds_ReturnNothing()
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, Guid.NewGuid(), StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [Guid.NewGuid(), Guid.NewGuid()], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EmptyEntityIds_ReturnNothing()
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, Guid.NewGuid(), StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ExcludesRowsWithoutAnEntityId()
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.TargetHoursDrift, AgentConditionStatus.Executed, entityId: null, handledAtUtc: StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [Guid.NewGuid()], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task RestrictedScope_ExcludesExecutedRowFromAForeignGroup_NegativeTest()
    {
        // NEGATIVE TEST: a planner scoped to one group root must not learn that Klacksy handled a
        // container in a group tree they cannot see.
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.Group.Add(new Group { Id = ForeignRootId, Root = null, Name = "foreign" });
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, StartUtc, ForeignRootId));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: false, visibleRootIds: new HashSet<Guid> { VisibleRootId });

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task RestrictedScope_IncludesExecutedRowFromAVisibleGroup()
    {
        // Paired with the negative test above: without this, a scope helper that excluded EVERYTHING under
        // restriction would still pass the suite.
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.Group.Add(new Group { Id = VisibleRootId, Root = null, Name = "visible" });
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, StartUtc, VisibleRootId));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: false, visibleRootIds: new HashSet<Guid> { VisibleRootId });

        result.Count.ShouldBe(1);
        result[0].EntityId.ShouldBe(entityId);
    }

    [Test]
    public async Task RestrictedScope_ExcludesGroupScopedKindWithoutAGroupId_ButAdminSeesIt()
    {
        // empty_container is in AgentTriggerGroupScopedKinds and is the only kind Etappe 5 can execute
        // today, so a row of that kind with no GroupId is the most likely real shape. It must stay with
        // admins instead of reaching every scoped planner - the rule the separate pre-filter Where exists
        // for, and therefore the check that extracting ApplyGroupScope kept it.
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, StartUtc, groupId: null));
        await context.SaveChangesAsync();

        var repository = new AgentConditionRepository(context);

        var restricted = await repository.GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: false, visibleRootIds: new HashSet<Guid> { VisibleRootId });
        restricted.ShouldBeEmpty();

        var admin = await repository.GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: true, visibleRootIds: NoRootIds);
        admin.Count.ShouldBe(1);
    }

    [Test]
    public async Task RestrictedScope_IncludesUngatedKindWithoutAGroupId()
    {
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.TargetHoursDrift, AgentConditionStatus.Executed, entityId, StartUtc, groupId: null));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: false, visibleRootIds: new HashSet<Guid> { VisibleRootId });

        result.Count.ShouldBe(1);
    }

    [Test]
    public async Task OrdersNewestHandlingFirst_WhenOneEntityCarriesSeveralExecutedRows()
    {
        // A re-detection after an Executed row opens a fresh row beside it (the partial unique index on
        // Fingerprint covers open statuses only), so one entity can legitimately carry several.
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, StartUtc.AddHours(1)));
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, StartUtc.AddHours(5)));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.Count.ShouldBe(2);
        result[0].HandledAtUtc.ShouldBe(StartUtc.AddHours(5));
    }

    [Test]
    public async Task OrdersAStampedRowAheadOfAnUnstampedOne()
    {
        // Pins the ordering CONTRACT, and nothing more: this cannot detect the provider divergence it
        // exists for. Postgres DESC is NULLS FIRST while the InMemory provider's LINQ-to-objects comparer
        // is nulls-last, so an implementation with a single OrderByDescending key would pass here and fail
        // in production, handing the grid the unstamped row. Only the explicit two-key sort in the
        // repository makes both providers agree; this test just holds the intended answer in place.
        var entityId = Guid.NewGuid();
        using var context = CreateContext();
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, handledAtUtc: null));
        context.AgentConditions.Add(Condition(
            AgentTriggerKinds.EmptyContainer, AgentConditionStatus.Executed, entityId, StartUtc.AddHours(2)));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetExecutedForEntitiesAsync(
            [entityId], isUnrestricted: true, visibleRootIds: NoRootIds);

        result.Count.ShouldBe(2);
        result[0].HandledAtUtc.ShouldBe(StartUtc.AddHours(2));
    }
}
