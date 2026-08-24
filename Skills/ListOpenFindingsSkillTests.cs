// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_open_findings: the not-a-planner fail-closed path, the empty-scope message, the
/// per-finding data shape (ActionRoute, AgeDays), and - the honesty requirement this skill exists for -
/// that a finding whose TriggerKind has no IAgentConditionFingerprintSource detector carries
/// ReconciliationTracked=false plus a StalenessNote, while a reconciled kind carries neither.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListOpenFindingsSkillTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private sealed class ReconciledFakeDetector : IAgentTriggerDetector, IAgentConditionFingerprintSource
    {
        public string Kind => AgentTriggerKinds.TargetHoursDrift;

        public Task<IReadOnlyList<IAgentTriggerEvent>> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IAgentTriggerEvent>>([]);

        public Task<IReadOnlySet<string>> GetActiveFingerprintsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class NonReconciledFakeDetector : IAgentTriggerDetector
    {
        public string Kind => AgentTriggerKinds.LockConflict;

        public Task<IReadOnlyList<IAgentTriggerEvent>> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IAgentTriggerEvent>>([]);
    }

    private IAgentConditionRepository _repository = null!;
    private IAgentConditionScopeResolver _scopeResolver = null!;
    private ListOpenFindingsSkill _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAgentConditionRepository>();
        _scopeResolver = Substitute.For<IAgentConditionScopeResolver>();
        var detectors = new List<IAgentTriggerDetector> { new ReconciledFakeDetector(), new NonReconciledFakeDetector() };
        _sut = new ListOpenFindingsSkill(_repository, _scopeResolver, detectors, new SettableTimeProvider(NowUtc));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string> { Roles.Authorised }
    };

    private static AgentCondition Finding(
        string kind,
        string severity,
        AgentConditionStatus status,
        DateTime detectedAtUtc,
        Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = kind,
        Fingerprint = Guid.NewGuid().ToString(),
        Severity = severity,
        Status = status,
        DetectedAtUtc = detectedAtUtc,
        LastSeenAtUtc = detectedAtUtc,
        GroupId = groupId,
        PayloadJson = "{}"
    };

    /// <summary>
    /// Result.Data wraps a private nested record (OpenFindingData), so a test outside the class cannot
    /// read its fields via a compile-time-typed cast or via `dynamic` (the runtime binder still enforces
    /// C# accessibility). Serializing to JSON and reading it back sidesteps that - reflection-based
    /// System.Text.Json does not check the declaring type's own accessibility - and matches how a real
    /// caller (the LLM/JSON tool-result pipeline) consumes SkillResult.Data anyway.
    /// </summary>
    private static JsonElement SingleFinding(SkillResult result)
    {
        var json = JsonSerializer.Serialize(result.Data);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Findings").EnumerateArray().Single().Clone();
    }

    [Test]
    public async Task NotAPlanner_ReturnsEmptyResultWithoutQueryingTheRepository()
    {
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("planning scope");
        await _repository.DidNotReceive().GetOpenForScopeAsync(Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Planner_WithNoOpenFindings_ReturnsExplicitEmptyMessage()
    {
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _repository.CountOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("No open findings");
    }

    [Test]
    public async Task Planner_PassesScopeAndDefaultLimitToTheRepository()
    {
        var visibleRootIds = new HashSet<Guid> { Guid.NewGuid() };
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Restricted(visibleRootIds));
        _repository.CountOpenForScopeAsync(false, visibleRootIds, Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetOpenForScopeAsync(false, visibleRootIds, 20, Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());

        await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        await _repository.Received(1).GetOpenForScopeAsync(false, visibleRootIds, 20, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Planner_WithExplicitLimit_PassesItToTheRepository()
    {
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _repository.CountOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), 5, Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());

        await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["limit"] = 5 });

        await _repository.Received(1).GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), 5, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReconciledKind_CarriesReconciliationTrackedTrue_AndNoStalenessNote()
    {
        var seeded = Finding(AgentTriggerKinds.TargetHoursDrift, AgentTriggerSeverity.High, AgentConditionStatus.Reported, NowUtc.AddDays(-3));
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _repository.CountOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>()).Returns(1);
        _repository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { seeded });

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var finding = SingleFinding(result);
        finding.GetProperty("ReconciliationTracked").GetBoolean().ShouldBeTrue();
        finding.GetProperty("StalenessNote").ValueKind.ShouldBe(JsonValueKind.Null);
        finding.GetProperty("AgeDays").GetInt32().ShouldBe(3);
        finding.GetProperty("ActionRoute").GetString().ShouldBe(ProactiveActionRoutes.Schedule);
        result.Message.ShouldNotContain("ReconciliationTracked=false");
    }

    [Test]
    public async Task NonReconciledKind_CarriesReconciliationTrackedFalse_WithStalenessNoteAndMessageCaveat()
    {
        var seeded = Finding(AgentTriggerKinds.LockConflict, AgentTriggerSeverity.High, AgentConditionStatus.Reported, NowUtc);
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _repository.CountOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>()).Returns(1);
        _repository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { seeded });

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var finding = SingleFinding(result);
        finding.GetProperty("ReconciliationTracked").GetBoolean().ShouldBeFalse();
        finding.GetProperty("StalenessNote").GetString().ShouldNotBeNullOrEmpty();
        result.Message.ShouldContain("ReconciliationTracked=false");
    }

    [Test]
    public async Task EscalatedFinding_IsIncludedInTheResult()
    {
        var seeded = Finding(AgentTriggerKinds.TargetHoursDrift, AgentTriggerSeverity.High, AgentConditionStatus.Escalated, NowUtc);
        seeded.AttemptCount = 3;
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _repository.CountOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>()).Returns(1);
        _repository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { seeded });

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var finding = SingleFinding(result);
        finding.GetProperty("Status").GetString().ShouldBe(nameof(AgentConditionStatus.Escalated));
        finding.GetProperty("AttemptCount").GetInt32().ShouldBe(3);
    }

    [Test]
    public async Task FindingWithoutAnActionRouteMapping_HasNullActionRoute()
    {
        const string unmappedKind = "some_future_kind_without_a_route";
        var seeded = Finding(unmappedKind, AgentTriggerSeverity.Low, AgentConditionStatus.Detected, NowUtc);
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _repository.CountOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>()).Returns(1);
        _repository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { seeded });

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var finding = SingleFinding(result);
        finding.GetProperty("ActionRoute").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
