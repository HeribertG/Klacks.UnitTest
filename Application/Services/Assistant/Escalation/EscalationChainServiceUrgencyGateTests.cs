// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the guard added when CoverAbsenceCommandHandler was wired to start a chain per absence day
/// regardless of how far out the shift is: a far-future report must not wake stage 1 with an
/// artificial "reply within MaxStageMinutes" deadline. The gate compares the deadline against what the
/// roster could reach even fully serial (roster size x MaxStageMinutes), using only values already in
/// EscalationTimeBudget - no separate "is this urgent" constant.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainServiceUrgencyGateTests
{
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTime ReportedAtUtc = new(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);

    private FakeEscalationChainRepository _repository = null!;
    private IEscalationRosterService _rosterService = null!;
    private IEscalationNotifier _notifier = null!;
    private ISettingsReader _settingsReader = null!;
    private SettableTimeProvider _timeProvider = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeEscalationChainRepository();
        _rosterService = Substitute.For<IEscalationRosterService>();
        _notifier = Substitute.For<IEscalationNotifier>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _timeProvider = new SettableTimeProvider(ReportedAtUtc);

        _rosterService.GetOrderedRosterAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterCandidate> { new("planner-a", "Planner A"), new("planner-b", "Planner B"), new("planner-c", "Planner C") });

        // Falls back to 5/30/2h -> reachable window = 3 x 30min = 90min.
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        _sut = new EscalationChainService(
            _repository, _rosterService, _notifier, _settingsReader, _timeProvider, Substitute.For<ILogger<EscalationChainService>>());
    }

    [Test]
    public async Task FarFutureShift_DeadlineBeyondReachableWindow_ChainNotStarted()
    {
        // Shift three weeks out; deadline is far beyond 3 x 30min from now.
        var shiftStartUtc = ReportedAtUtc.AddDays(21);

        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null));

        Assert.That(chainId, Is.Null);
        await _notifier.DidNotReceive().NotifyStageAsync(
            Arg.Any<Klacks.Api.Domain.Models.Assistant.Escalation.EscalationChain>(),
            Arg.Any<Klacks.Api.Domain.Models.Assistant.Escalation.EscalationStage>(),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeadlineExactlyAtReachableWindow_ChainStarts()
    {
        // Deadline - now == 90min == reachable window exactly: the boundary must still start (>, not >=).
        var shiftStartUtc = ReportedAtUtc.AddHours(2).AddMinutes(90);

        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null));

        Assert.That(chainId, Is.Not.Null);
    }
}
