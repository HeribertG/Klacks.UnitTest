// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Hand-written in-memory double for IEscalationChainRepository, used where the real conditional
/// ExecuteUpdateAsync semantics are not what is under test (see
/// EscalationChainServiceReferenceCaseTests for why: the EF in-memory provider does not support
/// ExecuteUpdateAsync, and this repository has no SQLite/Postgres harness). Mirrors the real
/// repository's "only transition when the expected prior status still holds" behaviour so
/// EscalationChainService's orchestration logic runs against realistic single-threaded semantics.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;

namespace Klacks.UnitTest.TestHelpers;

public sealed class FakeEscalationChainRepository : IEscalationChainRepository
{
    private readonly Dictionary<Guid, EscalationChain> _chains = new();
    private readonly HashSet<Guid> _deletedBreakIds = new();

    public EscalationChain GetChain(Guid chainId) => _chains[chainId];

    public EscalationStage GetStage(Guid chainId, string userId) =>
        _chains[chainId].Stages.Single(s => s.UserId == userId);

    public void MarkBreakDeleted(Guid breakId) => _deletedBreakIds.Add(breakId);

    public Task<bool> AddAsync(EscalationChain chain, CancellationToken cancellationToken = default)
    {
        if (_chains.Values.Any(c => c.WorkId == chain.WorkId && c.Status == EscalationChainStatus.Running))
        {
            return Task.FromResult(false);
        }

        _chains[chain.Id] = chain;
        return Task.FromResult(true);
    }

    public Task<EscalationChain?> GetByIdWithStagesAsync(Guid chainId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_chains.GetValueOrDefault(chainId));

    public Task<IReadOnlyList<EscalationStage>> GetStagesByChainAsync(Guid chainId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EscalationStage>>(
            _chains.TryGetValue(chainId, out var chain) ? chain.Stages.OrderBy(s => s.Rank).ToList() : []);

    public Task<IReadOnlyList<EscalationStage>> GetDueStagesAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EscalationStage>>(
            AllStages().Where(s => s.Status == EscalationStageStatus.Notified && s.DueAtUtc != null && s.DueAtUtc <= nowUtc).ToList());

    public Task<IReadOnlyList<Guid>> GetOverdueRunningChainIdsAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(
            _chains.Values.Where(c => c.Status == EscalationChainStatus.Running && c.DeadlineUtc <= nowUtc).Select(c => c.Id).ToList());

    public Task<IReadOnlyList<EscalationChain>> GetRunningChainsWithAbsenceBreakAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EscalationChain>>(
            _chains.Values.Where(c => c.Status == EscalationChainStatus.Running && c.AbsenceBreakId != null).ToList());

    public Task<IReadOnlyList<EscalationChain>> GetRunningChainsWithStagesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EscalationChain>>(
            _chains.Values.Where(c => c.Status == EscalationChainStatus.Running).OrderBy(c => c.DeadlineUtc).ToList());

    public Task<bool> IsBreakDeletedAsync(Guid breakId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_deletedBreakIds.Contains(breakId));

    public Task<EscalationStage?> FindNotifiedStageForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var stage = AllStages()
            .Where(s => s.UserId == userId && s.Status == EscalationStageStatus.Notified)
            .OrderByDescending(s => s.NotifiedAtUtc)
            .FirstOrDefault();

        if (stage is not null)
        {
            stage.Chain = _chains[stage.EscalationChainId];
        }

        return Task.FromResult(stage);
    }

    public Task<bool> TryNotifyStageAsync(
        Guid stageId, DateTime notifiedAtUtc, DateTime dueAtUtc, string? deliveryChannel, string? deliveryOutcome, Guid? dispatchRowId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TryTransitionStage(stageId, EscalationStageStatus.Pending, stage =>
        {
            stage.Status = EscalationStageStatus.Notified;
            stage.NotifiedAtUtc = notifiedAtUtc;
            stage.DueAtUtc = dueAtUtc;
            stage.DeliveryChannel = deliveryChannel;
            stage.DeliveryOutcome = deliveryOutcome;
            stage.DispatchRowId = dispatchRowId;
        }));
    }

    public Task<bool> TrySkipStageAsync(Guid stageId, string skipReason, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryTransitionStage(stageId, EscalationStageStatus.Pending, stage =>
        {
            stage.Status = EscalationStageStatus.Skipped;
            stage.SkipReason = skipReason;
        }));

    public Task<bool> TryExpireStageAsync(Guid stageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryTransitionStage(stageId, EscalationStageStatus.Notified, stage => stage.Status = EscalationStageStatus.Expired));

    public Task<bool> TryAcknowledgeStageAsync(Guid stageId, DateTime respondedAtUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryTransitionStage(stageId, EscalationStageStatus.Notified, stage =>
        {
            stage.Status = EscalationStageStatus.Acknowledged;
            stage.RespondedAtUtc = respondedAtUtc;
        }));

    public Task<bool> TryAcknowledgeChainAsync(
        Guid chainId, string userId, string userName, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TryTransitionChain(chainId, EscalationChainStatus.Running, chain =>
        {
            chain.Status = EscalationChainStatus.Acknowledged;
            chain.AcknowledgedByUserId = userId;
            chain.AcknowledgedByUserName = userName;
            chain.AcknowledgedAtUtc = atUtc;
        }));
    }

    public Task<int> CancelRemainingStagesAsync(Guid chainId, Guid exceptStageId, CancellationToken cancellationToken = default)
    {
        if (!_chains.TryGetValue(chainId, out var chain))
        {
            return Task.FromResult(0);
        }

        var affected = 0;
        foreach (var stage in chain.Stages)
        {
            if (stage.Id == exceptStageId
                || (stage.Status != EscalationStageStatus.Pending && stage.Status != EscalationStageStatus.Notified))
            {
                continue;
            }

            stage.Status = EscalationStageStatus.Cancelled;
            affected++;
        }

        return Task.FromResult(affected);
    }

    public Task<bool> TryExhaustChainAsync(Guid chainId, string outcomeReason, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryTransitionChain(chainId, EscalationChainStatus.Running, chain =>
        {
            chain.Status = EscalationChainStatus.Exhausted;
            chain.OutcomeReason = outcomeReason;
        }));

    public Task<bool> TrySupersedeChainAsync(Guid chainId, string outcomeReason, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryTransitionChain(chainId, EscalationChainStatus.Running, chain =>
        {
            chain.Status = EscalationChainStatus.Superseded;
            chain.OutcomeReason = outcomeReason;
        }));

    public Task<bool> TryCancelChainAsync(
        Guid chainId, string userId, string userName, string reason, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TryTransitionChain(chainId, EscalationChainStatus.Running, chain =>
        {
            chain.Status = EscalationChainStatus.Cancelled;
            chain.CancelledByUserId = userId;
            chain.CancelledByUserName = userName;
            chain.CancelReason = reason;
            chain.CancelledAtUtc = atUtc;
        }));
    }

    private IEnumerable<EscalationStage> AllStages() => _chains.Values.SelectMany(c => c.Stages);

    private bool TryTransitionStage(Guid stageId, EscalationStageStatus expected, Action<EscalationStage> apply)
    {
        var stage = AllStages().FirstOrDefault(s => s.Id == stageId);
        if (stage is null || stage.Status != expected)
        {
            return false;
        }

        apply(stage);
        return true;
    }

    private bool TryTransitionChain(Guid chainId, EscalationChainStatus expected, Action<EscalationChain> apply)
    {
        if (!_chains.TryGetValue(chainId, out var chain) || chain.Status != expected)
        {
            return false;
        }

        apply(chain);
        return true;
    }
}
