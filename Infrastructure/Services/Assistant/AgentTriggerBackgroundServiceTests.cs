// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Etappe 3c tick integration: the ledger upsert that runs as a sibling step BEFORE
/// the notification, the "only what is new gets announced" gate, the resolve reconciliation restricted
/// to detectors that promise a complete fingerprint set, and the exclusion of companion events from the
/// ledger altogether.
///
/// The ledger under test is the real AgentConditionLedgerService over FakeAgentConditionRepository
/// rather than a substituted interface, so these tests exercise the actual re-arm and touch rules; only
/// IAgentTriggerService is substituted, because what matters about it here is nothing but the count and
/// identity of the events it was handed. The scope factory is a real ServiceCollection, matching
/// BackgroundServiceShutdownTests - substituting IServiceScopeFactory would mean stubbing a whole
/// IServiceProvider chain for no gain.
///
/// Scope note - what this does NOT cover: the timer loop in ExecuteAsync (the 2 minute first-run delay
/// and the 60 minute interval are wall-clock Task.Delay calls with no TimeProvider seam), and the
/// per-user delivery dedup inside AgentTriggerService, which Etappe 3c deliberately leaves untouched as
/// a second, independent mechanism.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class AgentTriggerBackgroundServiceTests
{
    private const string TrackedKind = "unstaffed_shift";
    private const string CompanionKind = "curiosity_question";
    private static readonly DateTime StartUtc = new(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc);

    private FakeAgentConditionRepository _repository = null!;
    private SettableTimeProvider _timeProvider = null!;
    private IAgentConditionLedgerService _ledger = null!;
    private IAgentTriggerService _triggerService = null!;

    [SetUp]
    public void Setup()
    {
        _repository = new FakeAgentConditionRepository();
        _timeProvider = new SettableTimeProvider(StartUtc);
        _ledger = new AgentConditionLedgerService(
            _repository, _timeProvider, NullLogger<AgentConditionLedgerService>.Instance);
        _triggerService = Substitute.For<IAgentTriggerService>();
    }

    [Test]
    public async Task NewCondition_OpensALedgerRowAndIsAnnounced()
    {
        var triggerEvent = PlannerEvent("shift-42");
        var detector = new FakeDetector(TrackedKind, triggerEvent);

        await RunTickAsync(detector);

        _repository.Conditions.Count.ShouldBe(1);
        var stored = _repository.Conditions.Single();
        stored.TriggerKind.ShouldBe(TrackedKind);
        stored.Fingerprint.ShouldBe(AgentConditionLedgerPolicy.FingerprintFor(triggerEvent));
        stored.EntityId.ShouldBe(triggerEvent.EntityId);
        stored.PayloadJson.ShouldContain("shiftId");

        await _triggerService.Received(1).OnEventAsync(triggerEvent, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAnnouncedConditionMovesOnFromDetectedToReported()
    {
        var triggerEvent = PlannerEvent("shift-42");

        await RunTickAsync(new FakeDetector(TrackedKind, triggerEvent));

        var stored = _repository.Conditions.Single();
        stored.Status.ShouldBe(
            AgentConditionStatus.Reported,
            "A row that stays in Detected for the rest of its life would make the Reported to Prepared "
            + "claim later stages are specified around unreachable.");

        _repository.EventsFor(stored.Id)
            .Select(auditEvent => auditEvent.EventType)
            .ShouldBe(
                new[] { AgentConditionStatus.Detected.ToString(), AgentConditionStatus.Reported.ToString() },
                "Both lifecycle steps belong in the append-only audit history, in order.");
    }

    [Test]
    public async Task KnownCondition_IsNotAnnouncedAgainButItsLastSeenMovesForward()
    {
        var triggerEvent = PlannerEvent("shift-42");
        var detector = new FakeDetector(TrackedKind, triggerEvent);

        await RunTickAsync(detector);

        var secondTickUtc = StartUtc.AddHours(1);
        _timeProvider.Now = secondTickUtc;
        await RunTickAsync(detector);

        _repository.Conditions.Count.ShouldBe(
            1,
            "The same fingerprint must reuse its open row instead of opening a second one.");
        _repository.Conditions.Single().LastSeenAtUtc.ShouldBe(secondTickUtc);
        _repository.Conditions.Single().DetectedAtUtc.ShouldBe(StartUtc);

        await _triggerService.Received(1).OnEventAsync(triggerEvent, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompanionEvents_NeverReachTheLedgerAndAreAllAnnounced()
    {
        var firstUser = CompanionEvent(Guid.NewGuid());
        var secondUser = CompanionEvent(Guid.NewGuid());
        var detector = new FakeDetector(CompanionKind, firstUser, secondUser);

        await RunTickAsync(detector);

        _repository.Conditions.ShouldBeEmpty(
            "A curiosity question is a per-user message, not world state. Both events share a DedupKey "
            + "(it defaults to the i18n summary), so a ledger row would fold the two users into one and "
            + "the is-new gate would silently swallow the second user's question.");

        await _triggerService.Received(1).OnEventAsync(firstUser, Arg.Any<CancellationToken>());
        await _triggerService.Received(1).OnEventAsync(secondUser, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFingerprintSourceResolvesTheRowsItNoLongerSees()
    {
        var stillThere = PlannerEvent("shift-1");
        var gone = PlannerEvent("shift-2");

        await RunTickAsync(new FakeFingerprintSourceDetector(
            TrackedKind,
            new IAgentTriggerEvent[] { stillThere, gone },
            FingerprintsOf(stillThere, gone)));

        _repository.Conditions.Count.ShouldBe(2);

        _timeProvider.Now = StartUtc.AddHours(1);
        await RunTickAsync(new FakeFingerprintSourceDetector(
            TrackedKind,
            new IAgentTriggerEvent[] { stillThere },
            FingerprintsOf(stillThere)));

        StatusOf(stillThere).ShouldBe(AgentConditionStatus.Reported);
        StatusOf(gone).ShouldBe(AgentConditionStatus.Resolved);
    }

    [Test]
    public async Task AFingerprintSourceResolvesEvenWhenItFoundNothingAtAll()
    {
        var condition = PlannerEvent("shift-1");

        await RunTickAsync(new FakeFingerprintSourceDetector(
            TrackedKind, new IAgentTriggerEvent[] { condition }, FingerprintsOf(condition)));

        _timeProvider.Now = StartUtc.AddHours(1);
        await RunTickAsync(new FakeFingerprintSourceDetector(
            TrackedKind, Array.Empty<IAgentTriggerEvent>(), new HashSet<string>(StringComparer.Ordinal)));

        StatusOf(condition).ShouldBe(
            AgentConditionStatus.Resolved,
            "Everything got fixed is exactly the case resolution exists for, and it arrives as an empty "
            + "event list. Gating the reconciliation on a non-empty result would never fire it.");
    }

    [Test]
    public async Task ADetectorWithoutAFingerprintSourceNeverResolvesAnything()
    {
        var condition = PlannerEvent("shift-1");

        await RunTickAsync(new FakeDetector(TrackedKind, condition));

        _timeProvider.Now = StartUtc.AddHours(1);
        await RunTickAsync(new FakeDetector(TrackedKind));

        StatusOf(condition).ShouldBe(
            AgentConditionStatus.Reported,
            "Without a complete fingerprint set, absence from a capped scan says nothing about whether "
            + "the condition is gone. The row must stay open rather than be resolved on a guess.");
    }

    [Test]
    public async Task AFailingDetectorDoesNotSuppressTheDetectorsBehindIt()
    {
        var survivor = PlannerEvent("shift-9");

        await RunTickAsync(
            new ThrowingDetector("lock_conflict"),
            new FakeDetector(TrackedKind, survivor));

        _repository.Conditions.Count.ShouldBe(1);
        await _triggerService.Received(1).OnEventAsync(survivor, Arg.Any<CancellationToken>());
    }

    private async Task RunTickAsync(params IAgentTriggerDetector[] detectors)
    {
        var services = new ServiceCollection();
        foreach (var detector in detectors)
        {
            services.AddScoped(_ => detector);
        }

        services.AddScoped(_ => _triggerService);
        services.AddScoped(_ => _ledger);

        using var provider = services.BuildServiceProvider();
        using var sut = new AgentTriggerBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentTriggerBackgroundService>.Instance);

        await sut.RunTickAsync(CancellationToken.None);
    }

    private AgentConditionStatus StatusOf(IAgentTriggerEvent triggerEvent) =>
        _repository.Conditions
            .Single(condition => condition.Fingerprint == AgentConditionLedgerPolicy.FingerprintFor(triggerEvent))
            .Status;

    private static IReadOnlySet<string> FingerprintsOf(params IAgentTriggerEvent[] events) =>
        events.Select(AgentConditionLedgerPolicy.FingerprintFor).ToHashSet(StringComparer.Ordinal);

    private static TestTriggerEvent PlannerEvent(string dedupKey) =>
        new(TrackedKind, dedupKey) { PlannersOnly = true, EntityId = Guid.NewGuid() };

    private static TestTriggerEvent CompanionEvent(Guid userId) =>
        new(CompanionKind, "same-question-for-everyone") { TargetUserId = userId };

    private sealed record TestTriggerEvent(string Kind, string DedupKey) : IAgentTriggerEvent
    {
        public string Severity => "low";

        public string Summary => "test";

        public bool PlannersOnly { get; init; }

        public Guid? TargetUserId { get; init; }

        public Guid? EntityId { get; init; }

        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>
        {
            ["shiftId"] = EntityId
        };
    }

    private class FakeDetector : IAgentTriggerDetector
    {
        private readonly IReadOnlyList<IAgentTriggerEvent> _events;

        public FakeDetector(string kind, params IAgentTriggerEvent[] events)
        {
            Kind = kind;
            _events = events;
        }

        public string Kind { get; }

        public Task<IReadOnlyList<IAgentTriggerEvent>> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_events);
    }

    private sealed class FakeFingerprintSourceDetector : FakeDetector, IAgentConditionFingerprintSource
    {
        private readonly IReadOnlySet<string> _fingerprints;

        public FakeFingerprintSourceDetector(
            string kind,
            IReadOnlyList<IAgentTriggerEvent> events,
            IReadOnlySet<string> fingerprints)
            : base(kind, events.ToArray())
        {
            _fingerprints = fingerprints;
        }

        public Task<IReadOnlySet<string>> GetActiveFingerprintsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_fingerprints);
    }

    private sealed class ThrowingDetector : IAgentTriggerDetector
    {
        public ThrowingDetector(string kind)
        {
            Kind = kind;
        }

        public string Kind { get; }

        public Task<IReadOnlyList<IAgentTriggerEvent>> DetectAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("detector blew up");
    }
}
