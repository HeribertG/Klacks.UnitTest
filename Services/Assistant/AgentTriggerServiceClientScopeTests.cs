// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the per-employee audience of the client-scoped proactive events (hours drift, availability gap,
/// missing client core data, expiring contract). Their content names employees, so the recipient set is no
/// longer the unscoped planner list: every affected employee is resolved through
/// IPlanningAudienceResolver.GetPlanningUserIdsForClientAsync, an Admin receives the full finding, a
/// supervisor receives only the employees they may see (count, names and severity recomputed), and a
/// supervisor who may see none of them receives nothing. Dedup key, content key and condition id stay the
/// event's own for every variant, so the reminder and acknowledge loop keeps working on a narrowed row.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AgentTriggerServiceClientScopeTests
{
    private const string Admin = "admin-a";
    private const string SupervisorOfAnn = "supervisor-ann";
    private const string SupervisorOfNobody = "supervisor-none";

    private static readonly Guid AnnId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();
    private static readonly DateTime FakeNow = new(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc);

    private IAgentTriggerRateLimiter _rateLimiter = null!;
    private IAgentTriggerPreferenceService _preferenceService = null!;
    private IAssistantNotificationService _notificationService = null!;
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IProactiveMessengerTextComposer _messengerTextComposer = null!;
    private List<ProactiveTriggerDispatchRow> _recorded = null!;
    private AgentTriggerService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        _preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _recorded = [];
        _dispatchRepository
            .When(repository => repository.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(call => _recorded.Add(call.Arg<ProactiveTriggerDispatchRow>()));
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _messengerTextComposer = Substitute.For<IProactiveMessengerTextComposer>();
        var offlineMessengerNotifier = Substitute.For<IOfflineMessengerNotifier>();
        offlineMessengerNotifier
            .TrySendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OfflineMessengerDeliveryResult.ChannelUnavailable);

        SetClientAudience(AnnId, Admin, SupervisorOfAnn);
        SetClientAudience(BobId, Admin);
        _audienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Set(Admin, SupervisorOfAnn, SupervisorOfNobody));

        _sut = new AgentTriggerService(
            _rateLimiter, _preferenceService, _notificationService, _dispatchRepository, _conditionRepository,
            Substitute.For<IUserActivityTracker>(), _audienceResolver, offlineMessengerNotifier,
            _messengerTextComposer, new SettableTimeProvider(FakeNow), new RecordingLogger<AgentTriggerService>());
    }

    private static IReadOnlySet<string> Set(params string[] userIds) =>
        new HashSet<string>(userIds, StringComparer.OrdinalIgnoreCase);

    private void SetClientAudience(Guid clientId, params string[] userIds) =>
        _audienceResolver.GetPlanningUserIdsForClientAsync(clientId, Arg.Any<CancellationToken>()).Returns(Set(userIds));

    private ProactiveTriggerDispatchRow? RowFor(string userId) =>
        _recorded.SingleOrDefault(row => row.UserId == userId);

    private static Dictionary<string, string> ContentParams(ProactiveTriggerDispatchRow row) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.ContentParamsJson!)!;

    public static IEnumerable<TestCaseData> AggregateEvents()
    {
        yield return new TestCaseData(new TargetHoursDriftTriggerEvent(
            [new TargetHoursDriftAffectedClient(AnnId, "Ann", 13m), new TargetHoursDriftAffectedClient(BobId, "Bob", -30m)],
            "2026-08")).SetName("TargetHoursDrift");
        yield return new TestCaseData(new AvailabilityGapSummaryTriggerEvent(
            [new ProactiveAffectedClient(AnnId, "Ann"), new ProactiveAffectedClient(BobId, "Bob")],
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 22)).SetName("AvailabilityGapSummary");
        yield return new TestCaseData(new ClientMissingCoreDataSummaryTriggerEvent(
            [new ProactiveAffectedClient(AnnId, "Ann"), new ProactiveAffectedClient(BobId, "Bob")],
            ClientMissingCoreDataTriggerEvent.AddressField)).SetName("ClientMissingCoreDataSummary");
    }

    [TestCaseSource(nameof(AggregateEvents))]
    public async Task AggregateEvent_AdminGetsEveryEmployee_SupervisorOnlyTheVisibleOne_OtherSupervisorNothing(
        IAgentTriggerEvent triggerEvent)
    {
        await _sut.OnEventAsync(triggerEvent);

        var adminParams = ContentParams(RowFor(Admin).ShouldNotBeNull());
        adminParams["count"].ShouldBe("2");
        adminParams["names"].ShouldContain("Ann");
        adminParams["names"].ShouldContain("Bob");

        var supervisorParams = ContentParams(RowFor(SupervisorOfAnn).ShouldNotBeNull());
        supervisorParams["count"].ShouldBe("1");
        supervisorParams["names"].ShouldContain("Ann");
        supervisorParams["names"].ShouldNotContain("Bob");

        RowFor(SupervisorOfNobody).ShouldBeNull();
        await _audienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsAsync(default);
    }

    [TestCaseSource(nameof(AggregateEvents))]
    public async Task AggregateEvent_EveryVariantKeepsTheEventsDedupKeyContentKeyAndCondition(IAgentTriggerEvent triggerEvent)
    {
        var conditionId = Guid.NewGuid();
        _conditionRepository.FindOpenByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });

        await _sut.OnEventAsync(triggerEvent);

        _recorded.Count.ShouldBe(2);
        _recorded.ShouldAllBe(row => row.DedupKey == triggerEvent.DedupKey
            && row.ContentKey == triggerEvent.Summary
            && row.ConditionId == conditionId
            && row.NextReminderAtUtc != null);
    }

    [TestCaseSource(nameof(AggregateEvents))]
    public async Task AggregateEvent_SupervisorWhoAlreadyHasTheRow_IsDedupedAgainstTheSameKey(IAgentTriggerEvent triggerEvent)
    {
        _dispatchRepository
            .WasDispatchedAsync(SupervisorOfAnn, triggerEvent.Kind, triggerEvent.DedupKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var outcome = await _sut.OnEventAsync(triggerEvent);

        outcome.Deduped.ShouldBe(1);
        RowFor(SupervisorOfAnn).ShouldBeNull();
        RowFor(Admin).ShouldNotBeNull();
    }

    [Test]
    public async Task TargetHoursDrift_SeverityFollowsTheVisibleEmployeesOnly()
    {
        await _sut.OnEventAsync(new TargetHoursDriftTriggerEvent(
            [new TargetHoursDriftAffectedClient(AnnId, "Ann", 13m), new TargetHoursDriftAffectedClient(BobId, "Bob", -30m)],
            "2026-08"));

        RowFor(Admin)!.Severity.ShouldBe(AgentTriggerSeverity.High);
        ContentParams(RowFor(Admin)!)["hours"].ShouldBe("-30.0");
        RowFor(SupervisorOfAnn)!.Severity.ShouldBe(AgentTriggerSeverity.Medium);
        ContentParams(RowFor(SupervisorOfAnn)!)["hours"].ShouldBe("+13.0");
    }

    [Test]
    public async Task AggregateEvent_ConnectedSupervisor_LivePushCarriesTheNarrowedParams()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { SupervisorOfAnn });

        await _sut.OnEventAsync(new TargetHoursDriftTriggerEvent(
            [new TargetHoursDriftAffectedClient(AnnId, "Ann", 30m), new TargetHoursDriftAffectedClient(BobId, "Bob", -40m)],
            "2026-08"));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            SupervisorOfAnn,
            Arg.Any<string>(),
            conversationId: null,
            contentParams: Arg.Is<IReadOnlyDictionary<string, string>?>(
                sent => sent != null && sent["count"] == "1" && sent["names"] == "Ann"),
            messageId: Arg.Any<string>(),
            kind: AgentTriggerKinds.TargetHoursDrift,
            actionRoute: Arg.Any<string>(),
            actionParams: Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task AggregateEvent_NobodyButAdminsSeeTheEmployees_OnlyAdminsAreReached()
    {
        SetClientAudience(AnnId, Admin);

        await _sut.OnEventAsync(new ClientMissingCoreDataSummaryTriggerEvent(
            [new ProactiveAffectedClient(AnnId, "Ann"), new ProactiveAffectedClient(BobId, "Bob")],
            ClientMissingCoreDataTriggerEvent.ContactField));

        _recorded.Select(row => row.UserId).ShouldBe([Admin]);
    }

    [Test]
    public async Task ContractExpiringSoon_ReachesOnlyTheClientsAudience()
    {
        await _sut.OnEventAsync(new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), BobId, "Bob", new DateOnly(2026, 8, 31), 21));

        _recorded.Select(row => row.UserId).ShouldBe([Admin]);
        await _audienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsAsync(default);
    }

    [Test]
    public async Task ContractExpiringSoon_VisibleClient_ReachesAdminAndItsSupervisorUnchanged()
    {
        await _sut.OnEventAsync(new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), AnnId, "Ann", new DateOnly(2026, 8, 31), 21));

        _recorded.Select(row => row.UserId).ShouldBe([Admin, SupervisorOfAnn], ignoreOrder: true);
        _recorded.ShouldAllBe(row => ContentParams(row)["name"] == "Ann");
    }
}
