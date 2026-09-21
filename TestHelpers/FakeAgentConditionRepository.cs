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
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.TestHelpers;

public sealed class FakeAgentConditionRepository : IAgentConditionRepository
{
    private readonly List<AgentCondition> _conditions = new();
    private readonly List<AgentConditionEvent> _events = new();

    private AgentCondition? _insertRaceWinner;
    private Guid? _transitionLoserId;
    private (Guid ConditionId, string PayloadJson)? _payloadRefreshOnNextTransition;

    public IReadOnlyList<AgentCondition> Conditions => _conditions;

    public AgentCondition Stored(Guid id) => _conditions.Single(c => c.Id == id);

    public IReadOnlyList<AgentConditionEvent> EventsFor(Guid conditionId) =>
        _events.Where(e => e.ConditionId == conditionId).ToList();

    /// <param name="groupIds">
    /// The row's full group set, mirroring what the real repository stores in agent_condition_groups.
    /// Default empty, which together with the default null GroupId is the "concerns no group" shape the
    /// existing ledger tests rely on. Passing groups also stamps the primary GroupId the way detection
    /// does, so a seeded row can never carry the inconsistent pair no production path produces.
    /// </param>
    public AgentCondition Seed(
        string triggerKind,
        string fingerprint,
        AgentConditionStatus status,
        DateTime detectedAtUtc,
        IReadOnlySet<Guid>? groupIds = null)
    {
        var groups = groupIds ?? new HashSet<Guid>();
        var id = Guid.NewGuid();

        var condition = new AgentCondition
        {
            Id = id,
            TriggerKind = triggerKind,
            Fingerprint = fingerprint,
            Severity = "low",
            Status = status,
            DetectedAtUtc = detectedAtUtc,
            LastSeenAtUtc = detectedAtUtc,
            GroupId = AgentConditionLedgerPolicy.PrimaryGroupIdFor(groups),
            Groups = GroupRows(id, groups),
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

    /// <summary>
    /// Makes the next transition of this row report false without writing anything - the shape the real
    /// repository's compare-and-swap has when another instance moved the row first, and also the shape
    /// of the false NEGATIVE the retrying execution strategy can produce after a committed transaction.
    /// Callers of the ledger must treat both the same way: skip the row, never retry it here.
    /// </summary>
    public void LoseNextTransitionFor(Guid conditionId)
    {
        _transitionLoserId = conditionId;
    }

    /// <summary>
    /// Reproduces a re-observation that rewrites PayloadJson between a caller's pre-flight read and its
    /// claim: the claim itself still succeeds, and the row a re-read finds afterwards carries the new
    /// payload. This is the only way the post-claim re-bind can be reached at all, because the claim
    /// transition does not touch the payload by itself.
    /// </summary>
    public void RefreshPayloadOnNextTransitionFor(Guid conditionId, string payloadJson)
    {
        _payloadRefreshOnNextTransition = (conditionId, payloadJson);
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

    public Task<List<AgentCondition>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var matches = _conditions
            .Where(c => ids.Contains(c.Id))
            .Select(Copy)
            .ToList();

        return Task.FromResult(matches);
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

    public Task<AgentCondition?> GetOpenForScopeByIdAsync(
        Guid id,
        bool isUnrestricted,
        IReadOnlySet<Guid> visibleRootIds,
        CancellationToken cancellationToken = default)
    {
        var match = ScopedPlannerRelevant(isUnrestricted, visibleRootIds).FirstOrDefault(c => c.Id == id);
        return Task.FromResult(match == null ? null : Copy(match));
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
    /// Simplification vs. the real repository: this fake has no Group table to resolve a group id's
    /// Nested Set root through, so it compares the group ids directly against visibleRootIds instead of
    /// via a root join. The rule itself is mirrored faithfully - a row is admitted when ANY of its groups
    /// matches, not when its primary GroupId does - so the fake cannot make the multi-group case look
    /// solved when it is not. Sufficient for the ledger-service tests built on this fake, none of which
    /// exercise group scoping; the real scope-filtering behaviour (root comparison, not flattened
    /// membership) is proven in AgentConditionRepositoryTests against a real EF InMemory-backed
    /// AgentConditionRepository.
    /// </summary>
    private IEnumerable<AgentCondition> ScopedPlannerRelevant(bool isUnrestricted, IReadOnlySet<Guid> visibleRootIds)
    {
        var relevant = _conditions.Where(c => AgentConditionPlannerRelevantStatuses.Values.Contains(c.Status));

        return ApplyGroupScope(relevant, isUnrestricted, visibleRootIds);
    }

    private static IEnumerable<AgentCondition> ApplyGroupScope(
        IEnumerable<AgentCondition> conditions,
        bool isUnrestricted,
        IReadOnlySet<Guid> visibleRootIds)
    {
        return isUnrestricted
            ? conditions
            : conditions.Where(c => c.GroupId.HasValue
                ? c.Groups.Any(group => visibleRootIds.Contains(group.GroupId))
                : !AgentTriggerGroupScopedKinds.Values.Contains(c.TriggerKind));
    }

    private static int SeverityRank(AgentCondition condition) =>
        condition.Severity == AgentTriggerSeverity.High ? 0 : condition.Severity == AgentTriggerSeverity.Medium ? 1 : 2;

    public Task<AgentCondition?> InsertAsync(
        AgentCondition condition,
        AgentConditionEvent detectionEvent,
        IReadOnlySet<Guid> groupIds,
        CancellationToken cancellationToken = default)
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

        condition.Groups = GroupRows(condition.Id, groupIds);

        _conditions.Add(condition);
        detectionEvent.ConditionId = condition.Id;
        _events.Add(detectionEvent);

        return Task.FromResult<AgentCondition?>(Copy(condition));
    }

    /// <summary>
    /// Mirrors the real repository's diff: nothing is written and false is returned when the stored set
    /// already equals the requested one. The return value is what the ledger-service tests assert the
    /// "no write on an unchanged set" rule against, so a fake that always reported true would make that
    /// rule untestable.
    /// </summary>
    public Task<bool> SyncGroupsAsync(
        Guid conditionId,
        IReadOnlySet<Guid> groupIds,
        CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == conditionId);
        if (stored == null)
        {
            return Task.FromResult(false);
        }

        if (stored.Groups.Select(group => group.GroupId).ToHashSet().SetEquals(groupIds))
        {
            return Task.FromResult(false);
        }

        stored.Groups = GroupRows(conditionId, groupIds);
        return Task.FromResult(true);
    }

    private static List<AgentConditionGroup> GroupRows(Guid conditionId, IEnumerable<Guid> groupIds) =>
        groupIds
            .Select(groupId => new AgentConditionGroup { ConditionId = conditionId, GroupId = groupId })
            .ToList();

    public Task<bool> TryTransitionAsync(
        Guid id,
        AgentConditionStatus fromStatus,
        AgentConditionStatus toStatus,
        AgentConditionTransitionFields? fields,
        AgentConditionEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        if (_transitionLoserId == id)
        {
            _transitionLoserId = null;
            return Task.FromResult(false);
        }

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
        stored.LastAttemptAtUtc = fields?.LastAttemptAtUtc ?? stored.LastAttemptAtUtc;
        stored.ApprovedByUserId = fields?.ApprovedByUserId ?? stored.ApprovedByUserId;
        stored.AttemptCount += fields?.AttemptIncrement ?? 0;

        auditEvent.ConditionId = id;
        _events.Add(auditEvent);

        if (_payloadRefreshOnNextTransition is { } refresh && refresh.ConditionId == id)
        {
            stored.PayloadJson = refresh.PayloadJson;
            _payloadRefreshOnNextTransition = null;
        }

        return Task.FromResult(true);
    }

    public Task<bool> TryReclaimStaleAsync(
        Guid id,
        DateTime staleBeforeUtc,
        DateTime claimedAtUtc,
        AgentConditionEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null
            || stored.Status != AgentConditionStatus.Prepared
            || stored.LastAttemptAtUtc is not { } lastAttemptAtUtc
            || lastAttemptAtUtc >= staleBeforeUtc)
        {
            return Task.FromResult(false);
        }

        stored.LastAttemptAtUtc = claimedAtUtc;
        stored.AttemptCount++;

        auditEvent.ConditionId = id;
        _events.Add(auditEvent);

        return Task.FromResult(true);
    }

    public Task<bool> TryStampApprovalAsync(
        Guid id, Guid approverUserId, DateTime approvedAtUtc, CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null || stored.Status != AgentConditionStatus.Reported || stored.ApprovedByUserId != null)
        {
            return Task.FromResult(false);
        }

        stored.ApprovedByUserId = approverUserId;
        stored.ApprovedAtUtc = approvedAtUtc;
        return Task.FromResult(true);
    }

    public Task<bool> TryClearApprovalAsync(Guid id, Guid approverUserId, CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null || stored.Status != AgentConditionStatus.Reported || stored.ApprovedByUserId != approverUserId)
        {
            return Task.FromResult(false);
        }

        stored.ApprovedByUserId = null;
        stored.ApprovedAtUtc = null;
        return Task.FromResult(true);
    }

    public Task<bool> TrySetCausedByAsync(Guid id, Guid causedByConditionId, CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null || stored.CausedByConditionId != null)
        {
            return Task.FromResult(false);
        }

        stored.CausedByConditionId = causedByConditionId;
        return Task.FromResult(true);
    }

    public Task<List<AgentCondition>> GetActionableByKindAsync(
        string triggerKind,
        int take,
        CancellationToken cancellationToken = default)
    {
        var matches = _conditions
            .Where(c => c.TriggerKind == triggerKind
                && (c.Status == AgentConditionStatus.Reported || c.Status == AgentConditionStatus.Prepared))
            .OrderBy(SeverityRank)
            .ThenBy(c => c.DetectedAtUtc)
            .Take(take)
            .Select(Copy)
            .ToList();

        return Task.FromResult(matches);
    }

    /// <summary>
    /// Mirrors the real repository's per-group scoping. A null groupId is the INSTALLATION-WIDE bucket -
    /// the conditions that carry no group at all - and never "any group": a fake that ignored the
    /// parameter would hand every group the same pooled number and hide the very defect the group-scoped
    /// budget tests exist to pin.
    /// </summary>
    public Task<int> CountActionClaimsAsync(
        string triggerKind,
        Guid? groupId,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var conditionIds = _conditions
            .Where(c => c.TriggerKind == triggerKind && c.GroupId == groupId)
            .Select(c => c.Id)
            .ToHashSet();

        var count = _events.Count(e =>
            conditionIds.Contains(e.ConditionId)
            && e.AtUtc >= sinceUtc
            && e.Detail != null
            && e.Detail.StartsWith(AgentConditionActionDefaults.ActionClaimDetailPrefix, StringComparison.Ordinal));

        return Task.FromResult(count);
    }

    public Task<List<AgentCondition>> GetExecutedForEntitiesAsync(
        IReadOnlyCollection<Guid> entityIds,
        bool isUnrestricted,
        IReadOnlySet<Guid> visibleRootIds,
        CancellationToken cancellationToken = default)
    {
        var requested = entityIds.ToHashSet();
        var executed = _conditions.Where(c => c.Status == AgentConditionStatus.Executed
            && c.EntityId.HasValue
            && requested.Contains(c.EntityId.Value));

        var matches = ApplyGroupScope(executed, isUnrestricted, visibleRootIds)
            .OrderByDescending(c => c.HandledAtUtc.HasValue)
            .ThenByDescending(c => c.HandledAtUtc)
            .Select(Copy)
            .ToList();

        return Task.FromResult(matches);
    }

    public Task<List<AgentCondition>> GetExecutedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        var matches = _conditions
            .Where(c => c.Status == AgentConditionStatus.Executed
                && c.HandledAtUtc != null
                && c.HandledAtUtc >= sinceUtc)
            .Select(Copy)
            .ToList();

        return Task.FromResult(matches);
    }

    public Task<bool> TouchLastSeenAsync(
        Guid id,
        DateTime seenAtUtc,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null || !AgentConditionStateMachine.IsOpen(stored.Status))
        {
            return Task.FromResult(false);
        }

        if (stored.LastSeenAtUtc >= seenAtUtc && payloadJson == null)
        {
            return Task.FromResult(false);
        }

        if (seenAtUtc > stored.LastSeenAtUtc)
        {
            stored.LastSeenAtUtc = seenAtUtc;
        }

        if (payloadJson != null)
        {
            stored.PayloadJson = payloadJson;
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetDelegationAsync(
        Guid id,
        ProactiveMaxAction maxAction,
        Guid delegatingUserId,
        CancellationToken cancellationToken = default)
    {
        var stored = _conditions.FirstOrDefault(c => c.Id == id);
        if (stored == null || !AgentConditionPlannerRelevantStatuses.Values.Contains(stored.Status))
        {
            return Task.FromResult(false);
        }

        stored.DelegatedMaxAction = maxAction;
        stored.DelegatedByUserId = delegatingUserId;
        return Task.FromResult(true);
    }

    public Task<AgentConditionEvent> InsertEventAsync(AgentConditionEvent conditionEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(conditionEvent);
        return Task.FromResult(conditionEvent);
    }

    public Task<(int Conditions, int Events)> SoftDeleteExpiredAsync(
        DateTime shortLivedCutoffUtc,
        DateTime longLivedCutoffUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var eligible = AgentLedgerRetentionPolicy.ConditionEligible(shortLivedCutoffUtc, longLivedCutoffUtc).Compile();
        var expired = _conditions.Where(c => !c.IsDeleted && eligible(c)).ToList();
        var expiredIds = expired.Select(c => c.Id).ToHashSet();

        var events = _events.Where(e => !e.IsDeleted && expiredIds.Contains(e.ConditionId)).ToList();
        foreach (var conditionEvent in events)
        {
            conditionEvent.IsDeleted = true;
            conditionEvent.DeletedTime = nowUtc;
        }

        foreach (var condition in expired)
        {
            condition.IsDeleted = true;
            condition.DeletedTime = nowUtc;
        }

        return Task.FromResult((expired.Count, events.Count));
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
        ApprovedByUserId = source.ApprovedByUserId,
        ApprovedAtUtc = source.ApprovedAtUtc,
        CausedByConditionId = source.CausedByConditionId,
        DelegatedMaxAction = source.DelegatedMaxAction,
        DelegatedByUserId = source.DelegatedByUserId,
        PayloadJson = source.PayloadJson,
        Groups = GroupRows(source.Id, source.Groups.Select(group => group.GroupId))
    };
}
