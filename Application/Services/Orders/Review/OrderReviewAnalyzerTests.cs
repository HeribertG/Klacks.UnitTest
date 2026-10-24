// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderReviewAnalyzer and OrderReviewBundler: full-day orders of one pattern form a bundle with the
/// proposed start 07:00 and the 3x8 split judged against the members' rules; an order already starting at 07:00 needs no
/// start decision; orders with blocking fields, without any group in sight, with a split allowed only for some members,
/// or whose pattern too few orders share, are outliers reviewed one by one; a group proposal and a qualification found
/// by name are carried; expenses always stay unasked.
/// </summary>

using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Models.Scheduling;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class OrderReviewAnalyzerTests
{
    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly TimeOnly Midnight = TimeOnly.MinValue;
    private static readonly TimeOnly Seven = new(7, 0);

    private static SchedulingPolicy Policy(double maxDailyHours = 10) =>
        new(TimeSpan.FromHours(11), TimeSpan.FromHours(maxDailyHours), 6, TimeSpan.FromHours(50), 2m);

    private static Shift FullDayOrder(string name, Guid? groupId = null, TimeOnly? start = null, int day = 2) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Abbreviation = "NW",
            Status = ShiftStatus.OriginalOrder,
            FromDate = new DateOnly(2026, 11, day),
            StartShift = start ?? Midnight,
            EndShift = start ?? Midnight,
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
            GroupItems = groupId.HasValue ? [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId.Value }] : [],
        };

    private static OrderReviewAnalysisInput Input(
        IReadOnlyList<Shift> orders,
        IReadOnlyList<SchedulingPolicy>? bernPolicies = null,
        Dictionary<Guid, IReadOnlyList<string>>? missing = null,
        Dictionary<Guid, OrderGroupAssignment>? proposals = null,
        IReadOnlyList<Qualification>? qualifications = null) =>
        new(
            orders,
            missing ?? new Dictionary<Guid, IReadOnlyList<string>>(),
            proposals ?? new Dictionary<Guid, OrderGroupAssignment>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string> { [BernGroupId] = "Bern" },
            new Dictionary<Guid, IReadOnlyList<SchedulingPolicy>> { [BernGroupId] = bernPolicies ?? [Policy(), Policy()] },
            qualifications ?? [],
            "de");

    [Test]
    public void ThreeFullDayOrdersOfOnePattern_FormABundle_WithStartSevenAndThreeByEight()
    {
        var orders = Enumerable.Range(2, 3).Select(day => FullDayOrder($"Wache {day}", BernGroupId, day: day)).ToList();

        var items = OrderReviewAnalyzer.Analyze(Input(orders));

        Assert.That(items.Select(i => i.ReviewKey), Is.All.EqualTo("B1"));
        var first = items[0];
        Assert.That(first.OutlierReason, Is.Null);
        Assert.That(first.ProposedStart, Is.EqualTo(Seven));
        Assert.That(first.ChosenStart, Is.Null);
        Assert.That(first.ProposedParts.Select(p => p.Start), Is.EqualTo(new[] { Seven, new TimeOnly(15, 0), new TimeOnly(23, 0) }));
        Assert.That(first.ChosenParts, Is.Null);
        Assert.That(first.SplitVerdict, Is.EqualTo(OrderSplitVerdictKind.Allowed));
        Assert.That(first.HasGroup, Is.True);
        Assert.That(first.CurrentGroupName, Is.EqualTo("Bern"));
        Assert.That(first.ExpenseStatus, Is.EqualTo(OrderReviewExpenseStatus.Unasked));
        Assert.That(first.ChosenQualificationIds, Is.Empty);
    }

    [Test]
    public void AnOrderAlreadyStartingAtSeven_NeedsNoStartDecision()
    {
        var item = OrderReviewAnalyzer.Analyze(Input([FullDayOrder("Wache", BernGroupId, Seven)])).Single();

        Assert.That(item.ProposedStart, Is.Null);
        Assert.That(item.ChosenStart, Is.EqualTo(Seven));
    }

    [Test]
    public void TwoOrdersOfOnePattern_AreReviewedOneByOne()
    {
        var orders = new[] { FullDayOrder("A", BernGroupId, day: 2), FullDayOrder("B", BernGroupId, day: 3) };

        var items = OrderReviewAnalyzer.Analyze(Input(orders));

        Assert.That(items.Select(i => i.ReviewKey), Is.EquivalentTo(new[] { "S1", "S2" }));
        Assert.That(items.Select(i => i.OutlierReason), Is.All.EqualTo(OrderReviewBundler.TooFewSharePatternReason));
    }

    [Test]
    public void Outliers_BlockingFields_NoGroup_AndASplitForSomeMembers_LeaveTheBundle()
    {
        var bundle = Enumerable.Range(2, 3).Select(day => FullDayOrder($"Wache {day}", BernGroupId, day: day)).ToList();
        var blocked = FullDayOrder("Ohne Kürzel", BernGroupId, day: 9);
        var groupless = FullDayOrder("Ohne Gruppe", day: 10);
        var missing = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [blocked.Id] = ["abbreviation"],
            [groupless.Id] = ["at least one group"],
        };

        var items = OrderReviewAnalyzer.Analyze(Input([.. bundle, blocked, groupless], missing: missing));

        var blockedItem = items.Single(i => i.OrderId == blocked.Id);
        Assert.That(blockedItem.ReviewKey, Does.StartWith("S"));
        Assert.That(blockedItem.BlockingFields, Is.EqualTo(new[] { "abbreviation" }));
        var grouplessItem = items.Single(i => i.OrderId == groupless.Id);
        Assert.That(grouplessItem.ReviewKey, Does.StartWith("S"));
        Assert.That(grouplessItem.BlockingFields, Is.Empty);
        Assert.That(grouplessItem.OutlierReason, Does.Contain("no group"));
        Assert.That(items.Count(i => i.ReviewKey == "B1"), Is.EqualTo(3));
    }

    [Test]
    public void ASplitAllowedForSomeMembers_IsAnOutlierWithTheCount()
    {
        var orders = Enumerable.Range(2, 3).Select(day => FullDayOrder($"Wache {day}", BernGroupId, day: day)).ToList();

        var items = OrderReviewAnalyzer.Analyze(Input(orders, bernPolicies: [Policy(), Policy(maxDailyHours: 6)]));

        Assert.That(items.Select(i => i.SplitVerdict), Is.All.EqualTo(OrderSplitVerdictKind.AllowedForSome));
        Assert.That(items[0].OutlierReason, Does.Contain("1 of 2"));
        Assert.That(items.Select(i => i.ReviewKey), Is.All.StartWith("S"));
    }

    [Test]
    public void AGroupProposal_AndAQualificationFoundByName_AreCarried()
    {
        var orders = Enumerable.Range(2, 3).Select(day => FullDayOrder($"Nachtwache Sanität {day}", day: day)).ToList();
        var proposals = orders.ToDictionary(
            o => o.Id, o => new OrderGroupAssignment(o.Id, o.Name, "Kunde", BernGroupId, "Bern", "city name", null));
        var name = new MultiLanguage();
        name.SetValue("de", "Sanität");
        var medic = new Qualification { Id = Guid.NewGuid(), Name = name };
        var missing = orders.ToDictionary(o => o.Id, _ => (IReadOnlyList<string>)["at least one group"]);

        var items = OrderReviewAnalyzer.Analyze(Input(orders, missing: missing, proposals: proposals, qualifications: [medic]));

        Assert.That(items.Select(i => i.ReviewKey), Is.All.EqualTo("B1"));
        Assert.That(items[0].ProposedGroupId, Is.EqualTo(BernGroupId));
        Assert.That(items[0].GroupSource, Is.EqualTo("city name"));
        Assert.That(items[0].ProposedQualificationIds, Is.EqualTo(new[] { medic.Id }));
        Assert.That(items[0].ProposedQualificationNames, Is.EqualTo(new[] { "Sanität" }));
        Assert.That(items[0].ChosenQualificationIds, Is.Null);
    }

    [Test]
    public void AnOrderThatIsNotFullDay_IsNotSplitAndKeepsItsStart()
    {
        var order = FullDayOrder("Früh", BernGroupId);
        order.StartShift = new TimeOnly(6, 0);
        order.EndShift = new TimeOnly(14, 0);

        var item = OrderReviewAnalyzer.Analyze(Input([order])).Single();

        Assert.That(item.IsFullDay, Is.False);
        Assert.That(item.ChosenStart, Is.EqualTo(new TimeOnly(6, 0)));
        Assert.That(item.ChosenParts, Is.Empty);
        Assert.That(item.SplitVerdict, Is.Null);
    }
}
