// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers EscalationChainService.AcknowledgeChainAsync, the intervention list's per-chain
/// acknowledgement entry point. Unlike AcknowledgeAsync (userwide lookup via
/// FindNotifiedStageForUserAsync, which can resolve the wrong chain when the same user holds a
/// Notified stage on more than one chain at once), this method must resolve the stage strictly WITHIN
/// the given chainId. Setup mirrors EscalationChainServiceReferenceCaseTests: roster
/// A(1)/B(2)/C(3), 2-hour prep buffer, default 5-30 minute caps.
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
public class EscalationChainServiceAcknowledgeChainTests
{
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

        // Arg.Any<Guid>() on purpose: the mutation test starts a second chain for the same roster
        // under a different GroupId is not needed, but tying this to one concrete GroupId would be a
        // trap the moment a test reuses this roster for a second StartChainAsync call.
        _rosterService.GetOrderedRosterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterCandidate>
            {
                new("planner-a", "Planner A"),
                new("planner-b", "Planner B"),
                new("planner-c", "Planner C")
            });
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Messenger"));

        _sut = new EscalationChainService(
            _repository, _rosterService, _notifier, _settingsReader, _timeProvider, Substitute.For<ILogger<EscalationChainService>>());
    }

    [Test]
    public async Task AcknowledgeChainAsync_UserHoldsNotifiedStageOnThisChain_Succeeds()
    {
        var chainId = await StartChain(Guid.NewGuid(), ShiftStartUtc);

        var acknowledged = await _sut.AcknowledgeChainAsync(chainId, "planner-a");

        Assert.That(acknowledged, Is.True);
        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Acknowledged));
        Assert.That(_repository.GetChain(chainId).AcknowledgedByUserId, Is.EqualTo("planner-a"));
        Assert.That(_repository.GetStage(chainId, "planner-a").Status, Is.EqualTo(EscalationStageStatus.Acknowledged));
        Assert.That(_repository.GetStage(chainId, "planner-b").Status, Is.EqualTo(EscalationStageStatus.Cancelled));
        Assert.That(_repository.GetStage(chainId, "planner-c").Status, Is.EqualTo(EscalationStageStatus.Cancelled));

        await _notifier.Received(1).NotifyHandoffAsync(
            Arg.Any<EscalationChain>(),
            Arg.Is<EscalationStage>(s => s.UserId == "planner-a"),
            Arg.Any<IReadOnlyList<EscalationStage>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeChainAsync_UnknownChainId_ReturnsFalse()
    {
        await StartChain(Guid.NewGuid(), ShiftStartUtc);

        var acknowledged = await _sut.AcknowledgeChainAsync(Guid.NewGuid(), "planner-a");

        Assert.That(acknowledged, Is.False);
    }

    [Test]
    public async Task AcknowledgeChainAsync_UserHasNoNotifiedStageOnThisChain_ReturnsFalse()
    {
        var chainId = await StartChain(Guid.NewGuid(), ShiftStartUtc);

        // Serial wave at chain start notifies only rank 1 (planner-a); planner-b is still Pending.
        Assert.That(_repository.GetStage(chainId, "planner-b").Status, Is.EqualTo(EscalationStageStatus.Pending));

        var acknowledged = await _sut.AcknowledgeChainAsync(chainId, "planner-b");

        Assert.That(acknowledged, Is.False);
        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(_repository.GetStage(chainId, "planner-b").Status, Is.EqualTo(EscalationStageStatus.Pending));
    }

    [Test]
    public async Task AcknowledgeChainAsync_UserNotifiedOnAnotherChainToo_OnlyTouchesTheRequestedChain()
    {
        // Chain A: report 03:00 for a 06:00 shift -> deadline 04:00, planner-x notified at 03:00.
        var chainAId = await StartChain(Guid.NewGuid(), ShiftStartUtc);

        // Chain B: a second, independent outage, reported 5 minutes later for a shift starting 5
        // minutes later too -> deadline 04:05, gap from "now" still 60min, fits the 90min reachable
        // window. planner-x is rank 1 again and gets notified at 03:05 - strictly AFTER chain A's
        // notification, so FindNotifiedStageForUserAsync's "most recent" lookup would resolve to
        // chain B, not chain A. That is exactly the bug AcknowledgeChainAsync must not reproduce.
        _timeProvider.Now = ReportedAtUtc.AddMinutes(5);
        var chainBId = await StartChain(Guid.NewGuid(), ShiftStartUtc.AddMinutes(5));

        Assert.That(_repository.GetStage(chainAId, "planner-a").Status, Is.EqualTo(EscalationStageStatus.Notified));
        Assert.That(_repository.GetStage(chainBId, "planner-a").Status, Is.EqualTo(EscalationStageStatus.Notified));

        var acknowledged = await _sut.AcknowledgeChainAsync(chainAId, "planner-a");

        Assert.That(acknowledged, Is.True);
        Assert.That(_repository.GetChain(chainAId).Status, Is.EqualTo(EscalationChainStatus.Acknowledged),
            "Chain A must be the one acknowledged - it is the chain the caller asked for.");
        Assert.That(_repository.GetChain(chainBId).Status, Is.EqualTo(EscalationChainStatus.Running),
            "Chain B must stay untouched even though the same user holds a Notified stage there too.");
        Assert.That(_repository.GetStage(chainBId, "planner-a").Status, Is.EqualTo(EscalationStageStatus.Notified),
            "Chain B's stage for this user must not be silently acknowledged along with chain A's.");
    }

    private async Task<Guid> StartChain(Guid workId, DateTime shiftStartUtc)
    {
        var startedChainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            workId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null));

        Assert.That(startedChainId, Is.Not.Null, "60min deadline gap must fit the 3-stage roster's 90min reachable window.");
        return startedChainId!.Value;
    }
}
