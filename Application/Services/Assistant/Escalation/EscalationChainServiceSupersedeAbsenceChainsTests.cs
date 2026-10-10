// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SupersedeAbsenceChainsForBreaksAsync: once the scenario that covers an absence is accepted, the running
/// absence-coverage call list of that absence's break ends as Superseded and its open stages are cancelled.
/// Chains of other breaks, of another purpose or already finished are left alone. Same in-memory fake as the
/// other chain tests, same caveat about ExecuteUpdate semantics.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainServiceSupersedeAbsenceChainsTests
{
    private const string Reason = "the scenario that covers the absence was accepted";

    private static readonly DateTime StartedAtUtc = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    private FakeEscalationChainRepository _repository = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeEscalationChainRepository();
        _sut = new EscalationChainService(
            _repository,
            Substitute.For<IEscalationRosterService>(),
            Substitute.For<IEscalationNotifier>(),
            Substitute.For<ISettingsReader>(),
            Substitute.For<IAgentConditionLedgerService>(),
            new SettableTimeProvider(StartedAtUtc),
            Substitute.For<ILogger<EscalationChainService>>());
    }

    private async Task<EscalationChain> AddChainAsync(
        Guid? breakId, EscalationChainPurpose purpose = EscalationChainPurpose.AbsenceCoverage)
    {
        var chain = new EscalationChain
        {
            Id = Guid.NewGuid(),
            Status = EscalationChainStatus.Running,
            Purpose = purpose,
            WorkId = Guid.NewGuid(),
            AbsenceBreakId = breakId,
            DeadlineUtc = StartedAtUtc.AddHours(2),
            Stages =
            [
                new EscalationStage { Id = Guid.NewGuid(), UserId = "planner", Status = EscalationStageStatus.Notified },
                new EscalationStage { Id = Guid.NewGuid(), UserId = "admin", Status = EscalationStageStatus.Pending },
            ],
        };
        await _repository.AddAsync(chain);
        return chain;
    }

    [Test]
    public async Task Supersedes_RunningAbsenceChain_OfTheGivenBreak_AndCancelsItsOpenStages()
    {
        var breakId = Guid.NewGuid();
        var chain = await AddChainAsync(breakId);

        var ended = await _sut.SupersedeAbsenceChainsForBreaksAsync([breakId], Reason);

        Assert.That(ended, Is.EqualTo(1));
        var stored = _repository.GetChain(chain.Id);
        Assert.That(stored.Status, Is.EqualTo(EscalationChainStatus.Superseded));
        Assert.That(stored.OutcomeReason, Is.EqualTo(Reason));
        Assert.That(stored.Stages.Select(s => s.Status), Is.All.EqualTo(EscalationStageStatus.Cancelled));
    }

    [Test]
    public async Task LeavesChainsOfOtherBreaks_AndChainsWithoutBreak_Running()
    {
        var coveredBreak = Guid.NewGuid();
        await AddChainAsync(coveredBreak);
        var otherChain = await AddChainAsync(Guid.NewGuid());
        var noBreakChain = await AddChainAsync(null);

        var ended = await _sut.SupersedeAbsenceChainsForBreaksAsync([coveredBreak], Reason);

        Assert.That(ended, Is.EqualTo(1));
        Assert.That(_repository.GetChain(otherChain.Id).Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(_repository.GetChain(noBreakChain.Id).Status, Is.EqualTo(EscalationChainStatus.Running));
    }

    [Test]
    public async Task LeavesProactiveApprovalChains_Running()
    {
        var breakId = Guid.NewGuid();
        var chain = await AddChainAsync(breakId, EscalationChainPurpose.ProactiveApproval);

        var ended = await _sut.SupersedeAbsenceChainsForBreaksAsync([breakId], Reason);

        Assert.That(ended, Is.EqualTo(0));
        Assert.That(_repository.GetChain(chain.Id).Status, Is.EqualTo(EscalationChainStatus.Running));
    }

    [Test]
    public async Task IgnoresChainsThatAlreadyEnded()
    {
        var breakId = Guid.NewGuid();
        var chain = await AddChainAsync(breakId);
        await _repository.TryCancelChainAsync(chain.Id, "u", "User", "cancelled", StartedAtUtc);

        var ended = await _sut.SupersedeAbsenceChainsForBreaksAsync([breakId], Reason);

        Assert.That(ended, Is.EqualTo(0));
        Assert.That(_repository.GetChain(chain.Id).Status, Is.EqualTo(EscalationChainStatus.Cancelled));
    }

    [Test]
    public async Task EmptyBreakList_EndsNothing()
    {
        var chain = await AddChainAsync(Guid.NewGuid());

        var ended = await _sut.SupersedeAbsenceChainsForBreaksAsync([], Reason);

        Assert.That(ended, Is.EqualTo(0));
        Assert.That(_repository.GetChain(chain.Id).Status, Is.EqualTo(EscalationChainStatus.Running));
    }
}
