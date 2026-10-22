// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the group-scoped aggregate path of AgentTriggerService (IGroupScopedAggregateTriggerEvent) and the
/// rising-count dedup (IRisingCountTriggerEvent), using the open-order summary: every recipient gets the counts of
/// exactly the orders they may see - an Admin all of them including the ungrouped ones, a planner the orders of the
/// groups their GroupVisibility covers, a planner who may see none nothing at all. Within one company day a recipient
/// gets a further summary only when their own count rose above the highest count already delivered to them; the same
/// or a lower count sends nothing, and the next day starts afresh. A dispatch that loses the unique-index race to a
/// concurrent writer of the same row is counted as deduped, never logged as a lost notification, and a summary row never
/// enters the reminder loop. The dispatch
/// repository substitute answers the dedup reads from the rows recorded in the test, like the real table would.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AgentTriggerServiceGroupScopedTests
{
    private const string Admin = "admin";
    private const string PlannerA = "planner-a";
    private const string PlannerB = "planner-b";
    private const string SecondPlannerA = "planner-a2";
    private const string PlannerWithoutOrders = "planner-c";

    private static readonly DateOnly Today = new(2026, 10, 22);
    private static readonly Guid GroupA = Guid.NewGuid();
    private static readonly Guid GroupB = Guid.NewGuid();

    private IAgentTriggerRateLimiter _rateLimiter = null!;
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private RecordingLogger<AgentTriggerService> _logger = null!;
    private List<ProactiveTriggerDispatchRow> _rows = null!;
    private AgentTriggerService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var notificationService = Substitute.For<IAssistantNotificationService>();
        notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());

        _rows = [];
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _dispatchRepository.RecordAsync(Arg.Do<ProactiveTriggerDispatchRow>(row => _rows.Add(row)), Arg.Any<CancellationToken>());
        _dispatchRepository
            .ListDedupKeysAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<string>)_rows
                .Where(row => row.UserId == call.ArgAt<string>(0)
                    && row.TriggerKind == call.ArgAt<string>(1)
                    && row.DedupKey.StartsWith(call.ArgAt<string>(2), StringComparison.Ordinal))
                .Select(row => row.DedupKey)
                .ToList());

        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _planningAudienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>()).Returns(Users(Admin));
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Users(Admin, PlannerA, SecondPlannerA, PlannerB, PlannerWithoutOrders));
        _planningAudienceResolver.GetPlanningUserIdsForGroupAsync(GroupA, Arg.Any<CancellationToken>())
            .Returns(Users(Admin, PlannerA, SecondPlannerA));
        _planningAudienceResolver.GetPlanningUserIdsForGroupAsync(GroupB, Arg.Any<CancellationToken>())
            .Returns(Users(Admin, PlannerB));

        _logger = new RecordingLogger<AgentTriggerService>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _sut = new AgentTriggerService(
            _rateLimiter, preferenceService, notificationService, _dispatchRepository,
            _conditionRepository, Substitute.For<IUserActivityTracker>(), _planningAudienceResolver,
            Substitute.For<IOfflineMessengerNotifier>(), Substitute.For<IProactiveMessengerTextComposer>(),
            new SettableTimeProvider(new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc)),
            _logger);
    }

    private static IReadOnlySet<string> Users(params string[] userIds) =>
        new HashSet<string>(userIds, StringComparer.OrdinalIgnoreCase);

    private static OpenOrderSnapshot Order(int daysFromToday, params Guid[] groupIds) =>
        new(Guid.NewGuid(), Today.AddDays(daysFromToday), groupIds);

    private static readonly IReadOnlyList<OpenOrderSnapshot> MorningOrders =
        [Order(1, GroupA), Order(20, GroupA), Order(2, GroupB), Order(3), Order(40)];

    private static OpenOrdersSummaryTriggerEvent Summary(IReadOnlyList<OpenOrderSnapshot> orders, DateOnly? companyDay = null) =>
        new(companyDay ?? Today, orders);

    private Dictionary<string, string> ParamsOf(string userId) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(_rows.Last(row => row.UserId == userId).ContentParamsJson!)!;

    private List<string> RecipientsSince(int rowCount) => _rows.Skip(rowCount).Select(row => row.UserId).ToList();

    [Test]
    public async Task OnEventAsync_EveryRecipientGetsTheCountsOfTheOrdersTheyMaySee()
    {
        await _sut.OnEventAsync(Summary(MorningOrders));

        Assert.That(ParamsOf(Admin), Is.EqualTo(new Dictionary<string, string> { ["count"] = "5", ["soonCount"] = "3" }),
            "Admins see every order, the ungrouped ones included.");
        Assert.That(ParamsOf(PlannerA), Is.EqualTo(new Dictionary<string, string> { ["count"] = "2", ["soonCount"] = "1" }));
        Assert.That(ParamsOf(SecondPlannerA), Is.EqualTo(ParamsOf(PlannerA)));
        Assert.That(ParamsOf(PlannerB), Is.EqualTo(new Dictionary<string, string> { ["count"] = "1", ["soonCount"] = "1" }));
    }

    [Test]
    public async Task OnEventAsync_PlannerWhoMaySeeNoOrder_GetsNothing()
    {
        await _sut.OnEventAsync(Summary(MorningOrders));

        Assert.That(_rows.Select(row => row.UserId), Is.EquivalentTo(new[] { Admin, PlannerA, SecondPlannerA, PlannerB }));
    }

    [Test]
    public async Task OnEventAsync_SeverityFollowsTheRecipientsOwnOrders()
    {
        await _sut.OnEventAsync(Summary([Order(1, GroupA), Order(30, GroupB)]));

        Assert.That(_rows.Single(row => row.UserId == PlannerA).Severity, Is.EqualTo(AgentTriggerSeverity.High));
        Assert.That(_rows.Single(row => row.UserId == PlannerB).Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(_rows.Single(row => row.UserId == Admin).Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task OnEventAsync_OnlyUngroupedOrders_ReachAdminsOnly()
    {
        await _sut.OnEventAsync(Summary([Order(1), Order(9)]));

        Assert.That(_rows.Select(row => row.UserId), Is.EqualTo(new[] { Admin }));
        Assert.That(ParamsOf(Admin)["count"], Is.EqualTo("2"));
    }

    [Test]
    public async Task OnEventAsync_EveryRowCarriesTheCompanyDayAndTheDeliveredCountAsDedupKey()
    {
        await _sut.OnEventAsync(Summary(MorningOrders));

        Assert.That(_rows.Single(row => row.UserId == Admin).DedupKey, Is.EqualTo("2026-10-22#5"));
        Assert.That(_rows.Single(row => row.UserId == PlannerA).DedupKey, Is.EqualTo("2026-10-22#2"));
        Assert.That(_rows.Select(row => row.TriggerKind).Distinct(), Is.EqualTo(new[] { AgentTriggerKinds.OpenOrder }));
        Assert.That(_rows.Select(row => row.ContentKey).Distinct(),
            Is.EqualTo(new[] { ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.OpenOrdersSummary }));
    }

    [Test]
    public async Task OnEventAsync_SameCountLaterThatDay_SendsNothing()
    {
        await _sut.OnEventAsync(Summary(MorningOrders));
        var morningRows = _rows.Count;

        var outcome = await _sut.OnEventAsync(Summary(MorningOrders));

        Assert.That(morningRows, Is.EqualTo(4));
        Assert.That(RecipientsSince(morningRows), Is.Empty);
        Assert.That(outcome.Deduped, Is.EqualTo(4));
    }

    [Test]
    public async Task OnEventAsync_CountRisesForSomeRecipients_SendsANewSummaryToExactlyThem()
    {
        await _sut.OnEventAsync(Summary(MorningOrders));
        var morningRows = _rows.Count;

        await _sut.OnEventAsync(Summary([.. MorningOrders, Order(4, GroupA)]));

        Assert.That(RecipientsSince(morningRows), Is.EquivalentTo(new[] { Admin, PlannerA, SecondPlannerA }),
            "Planner B's count did not change, so B gets nothing.");
        Assert.That(ParamsOf(PlannerA), Is.EqualTo(new Dictionary<string, string> { ["count"] = "3", ["soonCount"] = "2" }));
        Assert.That(_rows.Last(row => row.UserId == PlannerA).DedupKey, Is.EqualTo("2026-10-22#3"));
    }

    [Test]
    public async Task OnEventAsync_CountFallsThatDay_SendsNothing()
    {
        await _sut.OnEventAsync(Summary(MorningOrders));
        var morningRows = _rows.Count;

        await _sut.OnEventAsync(Summary(MorningOrders.Skip(1).ToList()));

        Assert.That(RecipientsSince(morningRows), Is.Empty);
    }

    [Test]
    public async Task OnEventAsync_CountRisesAgainAfterFalling_SendsOnlyAboveTheHighestDeliveredCount()
    {
        await _sut.OnEventAsync(Summary([Order(1, GroupB), Order(2, GroupB)]));
        await _sut.OnEventAsync(Summary([Order(1, GroupB)]));
        var rowsAfterFall = _rows.Count;

        await _sut.OnEventAsync(Summary([Order(1, GroupB), Order(5, GroupB)]));
        Assert.That(RecipientsSince(rowsAfterFall), Is.Empty, "Two again is not above the two already delivered.");

        await _sut.OnEventAsync(Summary([Order(1, GroupB), Order(5, GroupB), Order(6, GroupB)]));
        Assert.That(RecipientsSince(rowsAfterFall), Is.EquivalentTo(new[] { Admin, PlannerB }));
    }

    [Test]
    public async Task OnEventAsync_NextDay_StartsAfreshWithTheFirstSummary()
    {
        await _sut.OnEventAsync(Summary(MorningOrders));
        var firstDayRows = _rows.Count;

        await _sut.OnEventAsync(Summary(MorningOrders.Skip(1).ToList(), Today.AddDays(1)));

        Assert.That(RecipientsSince(firstDayRows), Is.EquivalentTo(new[] { Admin, PlannerA, SecondPlannerA, PlannerB }));
        Assert.That(_rows.Last(row => row.UserId == PlannerA).DedupKey, Is.EqualTo("2026-10-23#1"));
    }

    [Test]
    public async Task OnEventAsync_LinkedSummaryRows_NeverEnterTheReminderLoop()
    {
        // A newer summary of the same day supersedes the older one, so reminding of the older row would resurface
        // stale numbers; the rows are condition-linked (for dismissal and delegation) but carry no reminder due date.
        var conditionId = Guid.NewGuid();
        _conditionRepository.FindOpenByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId, TriggerKind = AgentTriggerKinds.OpenOrder, Fingerprint = "open_order:2026-10-22" });

        await _sut.OnEventAsync(Summary(MorningOrders));

        _rows.ShouldNotBeEmpty();
        _rows.ShouldAllBe(row => row.ConditionId == conditionId);
        _rows.ShouldAllBe(row => row.NextReminderAtUtc == null);
    }

    [Test]
    public async Task OnEventAsync_InsertLostToAConcurrentWriter_IsDedupedAndNotLoggedAsALostNotification()
    {
        _dispatchRepository
            .When(repository => repository.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("duplicate key value violates unique constraint"));
        _dispatchRepository
            .WasDispatchedAsync(Arg.Any<string>(), AgentTriggerKinds.OpenOrder, Arg.Is<string>(key => key.StartsWith("2026-10-22#")), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var outcome = await _sut.OnEventAsync(Summary(MorningOrders));

        Assert.That(outcome.Deduped, Is.EqualTo(4));
        Assert.That(outcome.Failed, Is.Zero);
        Assert.That(_logger.Entries.Where(entry => entry.Level == LogLevel.Error), Is.Empty);
    }

    [Test]
    public async Task OnEventAsync_InsertRejectedWithoutAConcurrentRow_IsStillReportedAsFailed()
    {
        _dispatchRepository
            .When(repository => repository.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("database down"));

        var outcome = await _sut.OnEventAsync(Summary(MorningOrders));

        Assert.That(outcome.Failed, Is.EqualTo(4));
        Assert.That(_logger.Entries.Count(entry => entry.Level == LogLevel.Error), Is.EqualTo(4));
    }
}
