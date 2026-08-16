// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the fix for a real crash: CoverAbsence re-run on a shift that already has a Running chain
/// used to hit the partial unique index on (WorkId) unconditionally in AddAsync's SaveChangesAsync,
/// throwing an unhandled DbUpdateException that failed the whole command after the Break/scenario work
/// had already committed. StartChainAsync now catches that (via the repository returning false) and
/// returns null instead - the existing chain is left running untouched.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainServiceDuplicateChainTests
{
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTime ReportedAtUtc = new(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ShiftStartUtc = new(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc);

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

        // 3-candidate roster so the 60min gap between report and deadline (04:00 - 03:00) fits the
        // reachable window (3 x 30min = 90min) - matches the reference case's own worked numbers.
        _rosterService.GetOrderedRosterAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterCandidate>
            {
                new("planner-a", "Planner A"), new("planner-b", "Planner B"), new("planner-c", "Planner C")
            });
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Messenger"));

        _sut = new EscalationChainService(
            _repository, _rosterService, _notifier, _settingsReader, _timeProvider, Substitute.For<ILogger<EscalationChainService>>());
    }

    [Test]
    public async Task SecondStartForSameWorkId_ReturnsNull_LeavesFirstChainRunningUntouched()
    {
        var firstRequest = new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", ShiftStartUtc, AbsenceBreakId: null);

        var firstChainId = await _sut.StartChainAsync(firstRequest);
        Assert.That(firstChainId, Is.Not.Null);

        var secondChainId = await _sut.StartChainAsync(firstRequest);

        Assert.That(secondChainId, Is.Null, "A second chain for the same WorkId must not be created.");
        Assert.That(_repository.GetChain(firstChainId!.Value).Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(_repository.GetStage(firstChainId.Value, "planner-a").Status, Is.EqualTo(EscalationStageStatus.Notified));

        await _notifier.Received(1).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
