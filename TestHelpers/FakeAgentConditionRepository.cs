// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// In-memory stand-in for IAgentConditionRepository used by the ledger-service tests.
///
/// WHAT IT PROVES AND WHAT IT DOES NOT: it reproduces the OBSERVABLE contract of the real repository -
/// a transition only lands when the stored status still equals the expected one, an insert is refused
/// while an open row already holds the fingerprint, apply-if-set field semantics, and reads that hand out
/// detached copies. It does NOT exercise ExecuteUpdateAsync, the database transaction around transition
/// plus audit event, or row-level atomicity under real concurrency: the EF in-memory provider supports
/// none of that, and this project has no Postgres harness. The compare-and-swap is proven against a real
/// database in Klacks.IntegrationTest/Infrastructure/Repositories/AgentConditionRepositoryCasTests.cs;
/// what the tests built on this fake prove is the SERVICE layer above it - the state machine guard, the
/// re-arm and resolve rules, and that a lost claim writes no audit event.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.TestHelpers;

public sealed class FakeAgentConditionRepository : IAgentConditionRepository
{
    private readonly List<AgentCondition> _conditions = new();
    private readonly List<AgentConditionEvent> _events = new();

    private AgentCondition? _insertRaceWinner;

    public IReadOnlyList<AgentCondition> Conditions => _conditions;

    public AgentCondition Stored(Guid id) => _conditions.Single(c => c.Id == id);

    public IReadOnlyList<AgentConditionEvent> EventsFor(Guid conditionId) =>
        _events.Where(e => e.ConditionId == conditionId).ToList();

    public AgentCondition Seed(
        string triggerKind,
        string fingerprint,
        AgentConditionStatus status,
        DateTime detectedAtUtc)
    {
        var condition = new AgentCondition
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

        _conditions.Add(condition);
        return condition;
    }

    /// <summary>
    /// Makes the next insert behave as the partial unique index does when another API instance opened a
    /// row for the same fingerprint between this caller's lookup and its insert: the insert is refused and
    /// the other instance's row is what a re-read finds.
    /// </summary>
    public void LoseNextInsertTo(AgentCondition winner)
    {
        _insertRaceWinner = winner;
    }

    public Task<AgentCondition?> FindOpenByFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        var match = _conditions.FirstOrDefault(c =>
            c.Fingerprint == fingerprint && AgentConditionStateMachine.IsOpen(c.Status));

        return Task.FromResult(match == null ? null : Copy(match));
    }

    public Task<AgentCondition?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var match = _conditions.FirstOrDefault(c => c.Id == id);

        return Task.FromResult(match == null ? null : Copy(match));
    }

    public Task<AgentCondition?> FindByScenarioIdAsync(Guid scenarioId, CancellationToken cancellationToken = default)
    {
        var match = _conditions
            .Where(c => c.ScenarioId == scenarioId)
            .OrderByDescending(c => c.DetectedAtUtc)
            .FirstOrDefault();

        return Task.FromResult(match == null ? null : Copy(match));
    }

    public Task<List<AgentCondition>> GetOpenByKindAsync(string triggerKind, CancellationToken cancellationToken = default)
    {
        var matches = _conditions
            .Where(c => c.TriggerKind == triggerKind && AgentConditionStateMachine.IsOpen(c.Status))
            .OrderBy(c => c.DetectedAtUtc)
            .Select(Copy)
            .ToList();

        return Task.FromResult(matches);
    }

    public Task<List<AgentCondition>> GetOpenForScopeAsync(
        bool isUnrestricted,
        IReadOnlySet<Guid> visibleRootIds,
        int take,
        CancellationToken cancellationToken = default)
    {
        var matches = ScopedPlannerRelevant(isUnrestricted, visibleRootIds)
            .OrderBy(SeverityRank)
            .ThenBy(c => c.DetectedAtUtc)
            .Take(take)
            .Select(Copy)
            .ToList();

        return Task.FromResult(matches);
    }

    public Task<int> CountOpenForScopeAsync(
        bool isUnrestricted,
        IReadOnlySet<Guid> visibleRootIds,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ScopedPlannerRelevant(isUnrestricted, visibleRootIds).Count());
    }

    public Task<IReadOnlyList<AgentCondition>> GetTopForContextAsync(
        bool isUnrestricted,
        IReadOnlySet<Guid> visibleRootIds,
        Guid? preferredGroupId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var contextSeverities = new HashSet<string>(StringComparer.Ordinal) { AgentTriggerSeverity.High, AgentTriggerSeverity.Medium };
        var matches = ScopedPlannerRelevant(isUnrestricted, visibleRootIds)
            .Where(c => contextSeverities.Contains(c.Severity))
            .OrderBy(c => preferredGroupId.HasValue && c.GroupId == preferredGroupId ? 0 : 1)
            .ThenBy(c => c.Severity == AgentTriggerSeverity.High ? 0 : 1)
            .ThenBy(c => c.DetectedAtUtc)
            .Take(take)
            .Select(Copy)
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentCondition>>(matches);
    }

    /// <summary>
    /// Simplification vs. the real repository: this fake has no Group table to resolve a GroupId's
    /// Nested Set root through, so it compares GroupId directly against visibleRootIds instead of via a
    /// root join. Sufficient for the ledger-service tests built on this fake, none of which exercise
    /// group scoping; the real scope-filtering behaviour (root comparison, not flattened membership) is
    /// proven in AgentConditionRepositoryTests against a real EF InMemory-backed AgentConditionRepository.
    /// </summary>
    private IEnumerable<AgentCondition> ScopedPlannerRelevant(bool isUnrestricted, IReadOnlySet<Guid> visibleRootIds)
    {
        var relevant = _conditions.Where(c => AgentConditionPlannerRelevantStatuses.Values.Contains(c.Status));

        return isUnrestricted
            ? relevant
            : relevant.Where(c => c.GroupId.HasValue
                ? visibleRootIds.Contains(c.GroupId.Value)
                : !AgentTriggerGroupScopedKinds.Values.Contains(c.TriggerKind));
    }

    private static int SeverityRank(AgentCondition condition) =>
        condition.Severity == AgentTriggerSeverity.High ? 0 : condition.Severity == AgentTriggerSeverity.Medium ? 1 : 2;

    public Task<AgentCondition?> InsertAsync(AgentCondition condition, AgentConditionEvent detectionEvent, CancellationToken cancellationToken = default)
    {
        if (_insertRaceWinner != null)
        {
            _conditions.Add(_insertRaceWinner);
            _insertRaceWinner = null;
            return Task.FromResult<AgentCondition?>(null);
        }

        var blocked = _conditions.Any(c =>
            c.Fingerprint == condition.Fingerprint && AgentConditionStateMachine.IsOpen(c.Status));

        if (blocked)
        {
            return Task.FromResult<AgentCondition?>(null);
        }

        _conditions.Add(condition);
        detectionEvent.ConditionId = condition.Id;
        _events.Add(detectionEvent);

        return Task.FromResult<AgentCondition?>(Copy(condition));
    }

    public Task<bool> TryTransitionAsync(
        Guid id,
        AgentConditionStatus fromStatus,
        AgentConditionStatus toStatus,
        AgentConditionTransitionFields? fields,
        AgentConditionEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null || stored.Status != fromStatus)
        {
            return Task.FromResult(false);
        }

        stored.Status = toStatus;
        stored.ResolvedAtUtc = fields?.ResolvedAtUtc ?? stored.ResolvedAtUtc;
        stored.HandledAtUtc = fields?.HandledAtUtc ?? stored.HandledAtUtc;
        stored.EscalatedAtUtc = fields?.EscalatedAtUtc ?? stored.EscalatedAtUtc;
        stored.ScenarioId = fields?.ScenarioId ?? stored.ScenarioId;
        stored.HandlingKind = fields?.HandlingKind ?? stored.HandlingKind;
        stored.RejectReason = fields?.RejectReason ?? stored.RejectReason;
        stored.RejectedByUserId = fields?.RejectedByUserId ?? stored.RejectedByUserId;

        auditEvent.ConditionId = id;
        _events.Add(auditEvent);

        return Task.FromResult(true);
    }

    public Task<bool> TouchLastSeenAsync(Guid id, DateTime seenAtUtc, CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null || !AgentConditionStateMachine.IsOpen(stored.Status) || stored.LastSeenAtUtc >= seenAtUtc)
        {
            return Task.FromResult(false);
        }

        stored.LastSeenAtUtc = seenAtUtc;
        return Task.FromResult(true);
    }

    public Task<AgentConditionEvent> InsertEventAsync(AgentConditionEvent conditionEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(conditionEvent);
        return Task.FromResult(conditionEvent);
    }

    private static AgentCondition Copy(AgentCondition source) => new()
    {
        Id = source.Id,
        TriggerKind = source.TriggerKind,
        Fingerprint = source.Fingerprint,
        EntityId = source.EntityId,
        GroupId = source.GroupId,
        Severity = source.Severity,
        Status = source.Status,
        DetectedAtUtc = source.DetectedAtUtc,
        LastSeenAtUtc = source.LastSeenAtUtc,
        ResolvedAtUtc = source.ResolvedAtUtc,
        HandledAtUtc = source.HandledAtUtc,
        HandlingKind = source.HandlingKind,
        ScenarioId = source.ScenarioId,
        AttemptCount = source.AttemptCount,
        LastAttemptAtUtc = source.LastAttemptAtUtc,
        EscalatedAtUtc = source.EscalatedAtUtc,
        RejectReason = source.RejectReason,
        RejectedByUserId = source.RejectedByUserId,
        CausedByConditionId = source.CausedByConditionId,
        DelegatedMaxAction = source.DelegatedMaxAction,
        DelegatedByUserId = source.DelegatedByUserId,
        PayloadJson = source.PayloadJson
    };
}
