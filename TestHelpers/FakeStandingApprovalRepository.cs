// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Hand-written IStandingApprovalRepository double, deliberately not Substitute.For: the questions the
/// action dispatcher asks it are "is this grant still valid" and "does it cover this scope", and a
/// substitute answering a stubbed row would make every expiry, revocation and wrong-scope test pass
/// without the rule ever running. This double applies the REAL predicate
/// (StandingApprovalPolicy.ActiveAt) and the real exact-GroupId match, so a test that seeds an expired,
/// revoked or foreign-group grant genuinely exercises the lookup the production repository translates
/// into SQL.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

public sealed class FakeStandingApprovalRepository : IStandingApprovalRepository
{
    private readonly List<StandingApproval> _rows = new();

    public IReadOnlyList<StandingApproval> Rows => _rows;

    /// <summary>
    /// How often the active-grant lookup was asked. A test that pins "the gates before the grant stop the
    /// row" needs to show the grant was never even consulted, which no return value can express.
    /// </summary>
    public int Lookups { get; private set; }

    public StandingApproval Seed(
        string triggerKind,
        Guid? groupId,
        Guid grantedByUserId,
        DateTime grantedAtUtc,
        DateTime expiresAtUtc,
        int dailyBudget = StandingApprovalDefaults.DefaultDailyBudget,
        DateTime? revokedAtUtc = null)
    {
        var approval = new StandingApproval
        {
            Id = Guid.NewGuid(),
            TriggerKind = triggerKind,
            GroupId = groupId,
            GrantedByUserId = grantedByUserId,
            GrantedAtUtc = grantedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            DailyBudget = dailyBudget,
            RevokedAtUtc = revokedAtUtc,
            RevokedByUserId = revokedAtUtc is null ? null : grantedByUserId
        };

        _rows.Add(approval);
        return approval;
    }

    public Task<StandingApproval?> FindActiveAsync(
        string triggerKind, Guid? groupId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        Lookups++;
        var isActive = StandingApprovalPolicy.ActiveAt(nowUtc).Compile();

        return Task.FromResult(_rows
            .Where(row => string.Equals(row.TriggerKind, triggerKind, StringComparison.Ordinal))
            .Where(row => row.GroupId == groupId)
            .Where(isActive)
            .OrderByDescending(row => row.GrantedAtUtc)
            .FirstOrDefault());
    }

    public Task<IReadOnlyList<StandingApproval>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StandingApproval>>(_rows
            .OrderByDescending(row => row.GrantedAtUtc)
            .ToList());

    public Task<StandingApproval?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.FirstOrDefault(row => row.Id == id));

    public Task AddAsync(StandingApproval approval, CancellationToken cancellationToken = default)
    {
        _rows.Add(approval);
        return Task.CompletedTask;
    }

    public Task<bool> TryRevokeAsync(
        Guid id, Guid revokedByUserId, DateTime revokedAtUtc, CancellationToken cancellationToken = default)
    {
        var stored = _rows.FirstOrDefault(row => row.Id == id && row.RevokedAtUtc is null);
        if (stored is null)
        {
            return Task.FromResult(false);
        }

        stored.RevokedAtUtc = revokedAtUtc;
        stored.RevokedByUserId = revokedByUserId;

        return Task.FromResult(true);
    }
}
