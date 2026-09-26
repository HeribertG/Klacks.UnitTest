// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Ledger lifecycle of the period_auto_close outcome rows. The detector reports exactly the fingerprints its own
/// DetectAsync emitted in the same tick as active: a blocked cause that still holds keeps its row (and the recipient
/// dedup on that row keeps the notification from repeating), a cause that is gone - or a "closed" report from the
/// previous tick - resolves. Without a preceding DetectAsync the evaluation cannot be repeated (it seals), so the
/// open rows are reported as active and nothing is resolved.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodAutoCloseDetectorTests
{
    private static readonly Guid GroupId = Guid.Parse("7a1d0000-0000-0000-0000-00000000000a");
    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 31);

    private IPeriodAutoCloseService _service = null!;
    private IAgentConditionRepository _conditions = null!;
    private PeriodAutoCloseDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _service = Substitute.For<IPeriodAutoCloseService>();
        _conditions = Substitute.For<IAgentConditionRepository>();
        _sut = new PeriodAutoCloseDetector(_service, _conditions);
    }

    [Test]
    public async Task ActiveFingerprints_AreExactlyTheEventsOfThisTick()
    {
        IAgentTriggerEvent[] events =
        [
            new PeriodAutoCloseBlockedTriggerEvent(GroupId, "Bern", Start, End, 2, PeriodAutoCloseBlockReason.OpenErrors),
            new PeriodAutoClosedTriggerEvent(Guid.NewGuid(), "Thun", Start, End, 1, Guid.NewGuid())
        ];
        _service.RunAsync(Arg.Any<CancellationToken>()).Returns(events);

        var emitted = await _sut.DetectAsync();
        var active = await _sut.GetActiveFingerprintsAsync();

        active.ShouldBe(emitted.Select(AgentConditionLedgerPolicy.FingerprintFor).ToHashSet(), ignoreOrder: true);
        await _conditions.DidNotReceiveWithAnyArgs().GetOpenByKindAsync(default!, default);
    }

    [Test]
    public async Task NothingEmittedThisTick_ResolvesEveryOutcomeRow()
    {
        _service.RunAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<IAgentTriggerEvent>());

        await _sut.DetectAsync();

        (await _sut.GetActiveFingerprintsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task WithoutAPrecedingDetect_KeepsEveryOpenRow()
    {
        const string openFingerprint = AgentTriggerKinds.PeriodAutoClose + ":x";
        _conditions.GetOpenByKindAsync(AgentTriggerKinds.PeriodAutoClose, Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { new() { Fingerprint = openFingerprint } });

        var active = await _sut.GetActiveFingerprintsAsync();

        active.ShouldBe(new[] { openFingerprint });
        await _service.DidNotReceiveWithAnyArgs().RunAsync(default);
    }
}
