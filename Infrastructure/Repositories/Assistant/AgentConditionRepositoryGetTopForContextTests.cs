// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentConditionRepository.GetTopForContextAsync (Etappe 3g context block): the cap is
/// enforced by the database query itself (Take), Low severity is excluded, group-scoped rows outside the
/// caller's visible root ids are excluded (negative test), an unrestricted (Admin) caller sees every
/// scope, and a preferred group is ranked first without widening or narrowing visibility.
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
public class AgentConditionRepositoryGetTopForContextTests
{
    private static readonly DateTime StartUtc = new(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid VisibleRootId = Guid.NewGuid();
    private static readonly Guid ForeignRootId = Guid.NewGuid();

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
        string kind, AgentConditionStatus status, string severity, DateTime detectedAtUtc, Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = kind,
        Fingerprint = $"{kind}:{Guid.NewGuid()}",
        Severity = severity,
        Status = status,
        GroupId = groupId,
        DetectedAtUtc = detectedAtUtc,
        LastSeenAtUtc = detectedAtUtc,
        PayloadJson = "{}"
    };

    [Test]
    public async Task CapsAtTake_WhenMoreCandidatesExist()
    {
        using var context = CreateContext();
        for (var i = 0; i < 5; i++)
        {
            context.AgentConditions.Add(
                Condition("open_order", AgentConditionStatus.Detected, "high", StartUtc.AddMinutes(i)));
        }
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), preferredGroupId: null, take: 3);

        result.Count.ShouldBe(3);
    }

    [Test]
    public async Task ExcludesLowSeverity()
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition("open_order", AgentConditionStatus.Detected, "low", StartUtc));
        context.AgentConditions.Add(Condition("empty_container", AgentConditionStatus.Detected, "medium", StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), preferredGroupId: null, take: 3);

        result.ShouldAllBe(c => c.Severity != "low");
        result.Count.ShouldBe(1);
    }

    [Test]
    public async Task ExcludesTerminalStatuses()
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition("open_order", AgentConditionStatus.Resolved, "high", StartUtc));
        context.AgentConditions.Add(Condition("open_order", AgentConditionStatus.Rejected, "high", StartUtc));
        context.AgentConditions.Add(Condition("open_order", AgentConditionStatus.Executed, "high", StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), preferredGroupId: null, take: 3);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task IncludesEscalated_UnlikeAgentConditionStateMachineOpenStatuses()
    {
        using var context = CreateContext();
        context.AgentConditions.Add(Condition("open_order", AgentConditionStatus.Escalated, "high", StartUtc));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), preferredGroupId: null, take: 3);

        result.Count.ShouldBe(1);
    }

    [Test]
    public async Task RestrictedScope_ExcludesConditionFromAForeignGroup_NegativeTest()
    {
        // NEGATIVE TEST (same fail-closed rule as 3e/PlanningAudienceResolver): a planner scoped to one
        // group root must never see a finding from a different, unrelated group tree.
        using var context = CreateContext();
        context.Group.Add(new Group { Id = ForeignRootId, Root = null, Name = "foreign" });
        context.AgentConditions.Add(
            Condition("empty_container", AgentConditionStatus.Detected, "high", StartUtc, ForeignRootId));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: false,
            visibleRootIds: new HashSet<Guid> { VisibleRootId },
            preferredGroupId: null,
            take: 3);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task RestrictedScope_IncludesConditionFromAVisibleGroup()
    {
        using var context = CreateContext();
        context.Group.Add(new Group { Id = VisibleRootId, Root = null, Name = "visible" });
        context.AgentConditions.Add(
            Condition("empty_container", AgentConditionStatus.Detected, "high", StartUtc, VisibleRootId));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: false,
            visibleRootIds: new HashSet<Guid> { VisibleRootId },
            preferredGroupId: null,
            take: 3);

        result.Count.ShouldBe(1);
    }

    [Test]
    public async Task RestrictedScope_IncludesUngatedCondition_EvenWithNoVisibleRoots()
    {
        // client_missing_core_data is about a client, not a group, and declares no RequiresGroupScope, so a
        // null GroupId here really does mean "the whole installation" and stays visible to every planner -
        // "Events ohne GroupId an alle Planer" from the 3e spec. A null GroupId on one of
        // AgentTriggerGroupScopedKinds.Values means the opposite and is withheld; that case is covered in
        // AgentConditionRepositoryTests.
        using var context = CreateContext();
        context.AgentConditions.Add(
            Condition(AgentTriggerKinds.ClientMissingCoreData, AgentConditionStatus.Detected, "high", StartUtc, groupId: null));
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: false,
            visibleRootIds: new HashSet<Guid>(),
            preferredGroupId: null,
            take: 3);

        result.Count.ShouldBe(1);
    }

    [Test]
    public async Task PreferredGroup_IsRankedFirst_WithoutExcludingOthers()
    {
        using var context = CreateContext();
        var preferredGroupId = Guid.NewGuid();
        context.Group.Add(new Group { Id = preferredGroupId, Root = null, Name = "preferred" });
        var preferred = Condition("open_order", AgentConditionStatus.Detected, "medium", StartUtc, preferredGroupId);
        var other = Condition("open_order", AgentConditionStatus.Detected, "high", StartUtc.AddMinutes(-30));
        context.AgentConditions.Add(preferred);
        context.AgentConditions.Add(other);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), preferredGroupId: preferredGroupId, take: 3);

        result[0].Id.ShouldBe(preferred.Id);
        result[1].Id.ShouldBe(other.Id);
    }

    [Test]
    public async Task WithoutPreferredGroup_OrdersBySeverityThenOldestFirst()
    {
        using var context = CreateContext();
        var oldHigh = Condition("open_order", AgentConditionStatus.Detected, "high", StartUtc.AddHours(-2));
        var newHigh = Condition("open_order", AgentConditionStatus.Detected, "high", StartUtc);
        var medium = Condition("empty_container", AgentConditionStatus.Detected, "medium", StartUtc.AddHours(-5));
        context.AgentConditions.Add(newHigh);
        context.AgentConditions.Add(medium);
        context.AgentConditions.Add(oldHigh);
        await context.SaveChangesAsync();

        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), preferredGroupId: null, take: 3);

        result[0].Id.ShouldBe(oldHigh.Id);
        result[1].Id.ShouldBe(newHigh.Id);
        result[2].Id.ShouldBe(medium.Id);
    }
}
