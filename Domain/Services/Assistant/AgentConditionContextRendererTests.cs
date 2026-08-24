// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentConditionContextRenderer: an empty or null list renders no block (empty string,
/// so callers omit it entirely rather than emitting an empty fragment), a row renders its severity,
/// trigger kind and LastSeenAtUtc, entity/group references are appended only when present, and the
/// wording never claims a condition is currently true - only that it was last observed.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AgentConditionContextRendererTests
{
    private static readonly DateTime LastSeenUtc = new(2026, 8, 24, 9, 30, 0, DateTimeKind.Utc);

    private static AgentCondition Condition(
        string kind, string severity, Guid? entityId = null, Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = kind,
        Fingerprint = $"{kind}:{Guid.NewGuid()}",
        Severity = severity,
        Status = AgentConditionStatus.Detected,
        EntityId = entityId,
        GroupId = groupId,
        DetectedAtUtc = LastSeenUtc,
        LastSeenAtUtc = LastSeenUtc,
        PayloadJson = "{}"
    };

    [Test]
    public void EmptyList_RendersNoBlock()
    {
        AgentConditionContextRenderer.Render(new List<AgentCondition>()).ShouldBe(string.Empty);
    }

    [Test]
    public void NullList_RendersNoBlock()
    {
        AgentConditionContextRenderer.Render(null).ShouldBe(string.Empty);
    }

    [Test]
    public void Row_RendersHeaderSeverityKindAndLastSeenTimestamp()
    {
        var block = AgentConditionContextRenderer.Render(new[] { Condition("open_order", "high") });

        block.ShouldContain("[OPEN_FINDINGS]");
        block.ShouldContain("[HIGH]");
        block.ShouldContain("open_order");
        block.ShouldContain("last observed");
        block.ShouldContain(LastSeenUtc.ToString("O"));
    }

    [Test]
    public void Row_NeverClaimsTheConditionIsCurrentlyTrue()
    {
        var block = AgentConditionContextRenderer.Render(new[] { Condition("empty_container", "medium") });

        block.ShouldNotContain("currently");
        block.ShouldNotContain("is happening now");
    }

    [Test]
    public void RowWithEntityAndGroup_RendersBothReferences()
    {
        var entityId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        var block = AgentConditionContextRenderer.Render(new[] { Condition("uncut_fullday_shift", "high", entityId, groupId) });

        block.ShouldContain($"entity {entityId}");
        block.ShouldContain($"group {groupId}");
    }

    [Test]
    public void RowWithoutEntityOrGroup_RendersNoParenthesesFragment()
    {
        var block = AgentConditionContextRenderer.Render(new[] { Condition("client_missing_core_data", "medium") });

        block.ShouldNotContain("()");
    }

    [Test]
    public void MultipleRows_KeepGivenOrder()
    {
        var first = Condition("open_order", "high");
        var second = Condition("empty_container", "medium");

        var block = AgentConditionContextRenderer.Render(new[] { first, second });

        block.IndexOf(first.Id.ToString(), StringComparison.Ordinal).ShouldBe(-1); // Id itself is not rendered
        block.IndexOf("open_order", StringComparison.Ordinal)
            .ShouldBeLessThan(block.IndexOf("empty_container", StringComparison.Ordinal));
    }
}
