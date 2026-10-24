// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderReviewDecisionApplier: a new start re-splits an equal split, a group is only given with full
/// access and only to orders without one, a split the members' rules forbid is refused and one allowed for some members
/// is stored with a hint, accepting proposals never takes a forbidden split, unknown qualifications are refused, and
/// declining expenses for all marks every order still unasked.
/// </summary>

using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.Api.Domain.Models.Scheduling;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class OrderReviewDecisionApplierTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly TimeOnly Seven = new(7, 0);

    private IGroupMemberPolicyReader _policyReader = null!;
    private IQualificationRepository _qualificationRepository = null!;
    private OrderReviewDecisionApplier _applier = null!;

    [SetUp]
    public void SetUp()
    {
        _policyReader = Substitute.For<IGroupMemberPolicyReader>();
        _policyReader.GetMemberPoliciesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchedulingPolicy> { Policy(10), Policy(10) });
        _qualificationRepository = Substitute.For<IQualificationRepository>();
        _qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Qualification>());
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc));
        _applier = new OrderReviewDecisionApplier(_policyReader, _qualificationRepository, clock);
    }

    private static SchedulingPolicy Policy(double maxDailyHours) =>
        new(TimeSpan.FromHours(11), TimeSpan.FromHours(maxDailyHours), 6, TimeSpan.FromHours(50), 2m);

    private static OrderReviewDraftItem FullDay(bool hasGroup = true) => new()
    {
        OrderId = Guid.NewGuid(),
        OrderName = "Wache",
        ReviewKey = "B1",
        IsFullDay = true,
        CurrentStart = TimeOnly.MinValue,
        CurrentEnd = TimeOnly.MinValue,
        ProposedStart = Seven,
        ProposedParts = FullDayOrderRule.EqualParts(Seven, 3),
        HasGroup = hasGroup,
        CurrentGroupId = hasGroup ? GroupId : null,
        CurrentGroupName = hasGroup ? "Bern" : null,
        ProposedGroupId = hasGroup ? null : GroupId,
        ProposedGroupName = hasGroup ? null : "Bern",
        ChosenQualificationIds = [],
    };

    private static OrderReviewDecision Decision(
        TimeOnly? start = null, string? split = null, string? group = null, string? qualifications = null,
        OrderReviewExpenseStatus? decline = null, bool accept = false) =>
        new("B1", start, split, null, group, qualifications, null, decline, accept, false);

    private static OrderReviewScope Scope(bool fullAccess) =>
        new(null, fullAccess, [new Group { Id = GroupId, Name = "Bern" }], [new Group { Id = GroupId, Name = "Bern" }],
            fullAccess ? OpenOrderSealingScope.FullAccess() : OpenOrderSealingScope.ForGroups([GroupId]));

    private Task<OrderReviewDecisionOutcome> Apply(OrderReviewDraftItem item, OrderReviewDecision decision, bool fullAccess = true) =>
        _applier.ApplyAsync(new OrderReviewDraft { Items = [item] }, [item], decision, Scope(fullAccess), "de");

    [Test]
    public async Task ANewStart_ResplitsAnEqualSplit()
    {
        var item = FullDay();
        item.ChosenParts = FullDayOrderRule.EqualParts(Seven, 3);

        await Apply(item, Decision(start: new TimeOnly(8, 0)));

        Assert.That(item.ChosenStart, Is.EqualTo(new TimeOnly(8, 0)));
        Assert.That(item.ChosenParts!.Select(p => p.Start), Is.EqualTo(new[] { new TimeOnly(8, 0), new TimeOnly(16, 0), TimeOnly.MinValue }));
    }

    [Test]
    public async Task AGroup_NeedsFullAccess_AndAnOrderWithoutGroup()
    {
        var withoutGroup = FullDay(hasGroup: false);
        var refused = await Apply(withoutGroup, Decision(group: "Bern"), fullAccess: false);
        Assert.That(refused.Rejected, Is.Not.Empty);
        Assert.That(withoutGroup.ChosenGroupId, Is.Null);

        await Apply(withoutGroup, Decision(group: "Bern"));
        Assert.That(withoutGroup.ChosenGroupId, Is.EqualTo(GroupId));

        var grouped = FullDay();
        var moved = await Apply(grouped, Decision(group: "Bern"));
        Assert.That(moved.Rejected.Single(), Does.Contain("already belongs"));
    }

    [Test]
    public async Task ASplitForbiddenForEveryMember_IsRefused_AndOneForSome_IsStoredWithAHint()
    {
        var forbidden = FullDay();
        var refused = await Apply(forbidden, Decision(split: "2x12"));
        Assert.That(refused.Rejected, Has.Count.EqualTo(1));
        Assert.That(forbidden.ChosenParts, Is.Null);

        _policyReader.GetMemberPoliciesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchedulingPolicy> { Policy(12), Policy(10) });
        var some = FullDay();
        var stored = await Apply(some, Decision(split: "2x12"));
        Assert.That(stored.Rejected, Is.Empty);
        Assert.That(stored.Hints.Single(), Does.Contain("1 of 2"));
        Assert.That(some.ChosenParts, Has.Count.EqualTo(2));
        Assert.That(some.SplitVerdict, Is.EqualTo(OrderSplitVerdictKind.AllowedForSome));
    }

    [Test]
    public async Task AcceptingProposals_TakesStartSplitAndGroup_ButNeverAForbiddenSplit()
    {
        var item = FullDay(hasGroup: false);
        await Apply(item, Decision(accept: true));
        Assert.That(item.ChosenStart, Is.EqualTo(Seven));
        Assert.That(item.ChosenParts, Has.Count.EqualTo(3));
        Assert.That(item.ChosenGroupId, Is.EqualTo(GroupId));

        var forbidden = FullDay();
        forbidden.SplitVerdict = OrderSplitVerdictKind.NotAllowed;
        var outcome = await Apply(forbidden, Decision(accept: true));
        Assert.That(forbidden.ChosenParts, Is.Null);
        Assert.That(outcome.Hints, Is.Not.Empty);
    }

    [Test]
    public async Task UnknownQualifications_AreRefused()
    {
        var item = FullDay();
        item.ChosenQualificationIds = null;

        var outcome = await Apply(item, Decision(qualifications: "Sanität"));

        Assert.That(outcome.Rejected.Single(), Does.Contain("unknown qualification"));
        Assert.That(item.ChosenQualificationIds, Is.Null);
    }

    [Test]
    public async Task DecliningExpensesForAll_MarksEveryOrderStillUnasked()
    {
        var target = FullDay();
        var other = FullDay();
        other.ReviewKey = "S1";
        var answered = FullDay();
        answered.ReviewKey = "S2";
        answered.ExpenseStatus = OrderReviewExpenseStatus.Provided;
        var draft = new OrderReviewDraft { Items = [target, other, answered] };

        await _applier.ApplyAsync(draft, [target], Decision(decline: OrderReviewExpenseStatus.DeclinedFinal), Scope(true), "de");

        Assert.That(target.ExpenseStatus, Is.EqualTo(OrderReviewExpenseStatus.DeclinedFinal));
        Assert.That(other.ExpenseStatus, Is.EqualTo(OrderReviewExpenseStatus.DeclinedFinal));
        Assert.That(answered.ExpenseStatus, Is.EqualTo(OrderReviewExpenseStatus.Provided));
    }
}
