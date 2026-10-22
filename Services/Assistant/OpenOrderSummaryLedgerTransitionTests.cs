// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what the switch from one message per order to one daily summary does to the condition ledger, by running
/// the real OpenOrderDetector through the real AgentTriggerDetectorRunner and AgentConditionLedgerService over
/// FakeAgentConditionRepository. The per-order rows of the former shape (fingerprint open_order:{shiftId}:{date})
/// leave the active fingerprint set on the first run and are resolved silently - no notification is raised for
/// them, and the reminder sweep stops a dispatch row whose condition is no longer open. The summary row is keyed
/// on the company day: it is reused within the day, resolved and replaced on the next day, and resolved when no
/// order is open any more.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class OpenOrderSummaryLedgerTransitionTests
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private InMemoryShiftStore _store = null!;
    private FakeAgentConditionRepository _repository = null!;
    private SettableTimeProvider _timeProvider = null!;
    private IAgentTriggerService _triggerService = null!;
    private AgentTriggerDetectorRunner _runner = null!;

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryShiftStore();
        _repository = new FakeAgentConditionRepository();
        _timeProvider = new SettableTimeProvider(Morning.UtcDateTime);
        _triggerService = Substitute.For<IAgentTriggerService>();
        _triggerService.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveDispatchOutcome.Empty);
        var ledger = new AgentConditionLedgerService(_repository, _timeProvider, NullLogger<AgentConditionLedgerService>.Instance);
        _runner = new AgentTriggerDetectorRunner(_triggerService, ledger, NullLogger<AgentTriggerDetectorRunner>.Instance);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    private OpenOrderDetector DetectorAt(DateTimeOffset instant) => new(
        _store.Reader,
        ShiftGroupScopeReaderStub.WithoutAnyGroups(),
        new FixedCompanyClock(instant, TimeZoneInfo.Utc),
        NullLogger<OpenOrderDetector>.Instance);

    private static Shift OpenOrder(DateOnly fromDate) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Order",
        Status = ShiftStatus.OriginalOrder,
        FromDate = fromDate
    };

    private AgentCondition SeedFormerPerOrderRow(Shift order, AgentConditionStatus status) =>
        _repository.Seed(
            AgentTriggerKinds.OpenOrder,
            AgentConditionLedgerPolicy.FingerprintFor(AgentTriggerKinds.OpenOrder, $"{order.Id}:{order.FromDate:yyyy-MM-dd}"),
            status,
            Morning.UtcDateTime.AddDays(-3));

    [Test]
    public async Task FirstRun_ResolvesEveryFormerPerOrderRow_AndOpensOneSummaryRow()
    {
        var orders = Enumerable.Range(1, 3).Select(offset => OpenOrder(new DateOnly(2026, 10, 22).AddDays(offset))).ToList();
        _store.Seed(orders);
        var formerRows = new[]
        {
            SeedFormerPerOrderRow(orders[0], AgentConditionStatus.Detected),
            SeedFormerPerOrderRow(orders[1], AgentConditionStatus.Reported),
            SeedFormerPerOrderRow(orders[2], AgentConditionStatus.Prepared)
        };

        var outcome = await _runner.RunAsync(DetectorAt(Morning));

        Assert.That(outcome.Resolved, Is.EqualTo(3));
        Assert.That(formerRows.Select(row => _repository.Stored(row.Id).Status),
            Is.All.EqualTo(AgentConditionStatus.Resolved));
        var open = _repository.Conditions.Where(row => AgentConditionStateMachine.IsOpen(row.Status)).ToList();
        Assert.That(open.Select(row => row.Fingerprint), Is.EqualTo(new[] { "open_order:2026-10-22" }));
        Assert.That(open.Single().Status, Is.EqualTo(AgentConditionStatus.Reported));
        Assert.That(open.Single().EntityId, Is.Null);
        Assert.That(open.Single().Severity, Is.EqualTo(AgentTriggerSeverity.Medium),
            "Orders start within seven days, yet the shared ledger row stays Medium so no planner's digest turns High through it.");
    }

    [Test]
    public async Task FirstRun_RaisesOnlyTheSummary_NeverANotificationForAResolvedFormerRow()
    {
        var order = OpenOrder(new DateOnly(2026, 10, 25));
        _store.Seed(order);
        SeedFormerPerOrderRow(order, AgentConditionStatus.Reported);

        await _runner.RunAsync(DetectorAt(Morning));

        await _triggerService.Received(1).OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<IAgentTriggerEvent>(e => e is OpenOrdersSummaryTriggerEvent), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SecondRunOfTheSameDay_ReusesTheSummaryRow()
    {
        _store.Seed(OpenOrder(new DateOnly(2026, 10, 25)));

        await _runner.RunAsync(DetectorAt(Morning));
        _timeProvider.Now = Morning.UtcDateTime.AddHours(5);
        await _runner.RunAsync(DetectorAt(Morning.AddHours(5)));

        Assert.That(_repository.Conditions, Has.Count.EqualTo(1));
        Assert.That(_repository.Conditions.Single().Status, Is.EqualTo(AgentConditionStatus.Reported));
    }

    [Test]
    public async Task NextDay_ResolvesYesterdaysSummaryRow_AndOpensTodaysOne()
    {
        _store.Seed(OpenOrder(new DateOnly(2026, 10, 30)));

        await _runner.RunAsync(DetectorAt(Morning));
        _timeProvider.Now = Morning.UtcDateTime.AddDays(1);
        await _runner.RunAsync(DetectorAt(Morning.AddDays(1)));

        var byFingerprint = _repository.Conditions.ToDictionary(row => row.Fingerprint, row => row.Status);
        Assert.That(byFingerprint["open_order:2026-10-22"], Is.EqualTo(AgentConditionStatus.Resolved));
        Assert.That(byFingerprint["open_order:2026-10-23"], Is.EqualTo(AgentConditionStatus.Reported));
    }

    [Test]
    public async Task NoOrderOpenAnyMore_ResolvesTodaysSummaryRow_WithoutANotification()
    {
        _store.Seed(OpenOrder(new DateOnly(2026, 10, 25)));
        await _runner.RunAsync(DetectorAt(Morning));
        _store.Context.Shift.RemoveRange(_store.Context.Shift);
        _store.Context.SaveChanges();
        _triggerService.ClearReceivedCalls();

        var outcome = await _runner.RunAsync(DetectorAt(Morning.AddHours(2)));

        Assert.That(outcome.Events, Is.Zero);
        Assert.That(outcome.Resolved, Is.EqualTo(1));
        Assert.That(_repository.Conditions.Single().Status, Is.EqualTo(AgentConditionStatus.Resolved));
        await _triggerService.DidNotReceive().OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
    }
}
